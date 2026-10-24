$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$stateRoot = Join-Path $root 'state'
$logsRoot = Join-Path $root 'logs'
$remote = 'https://github.com/pattymou/CSIT-System.git'
$currentFile = Join-Path $stateRoot 'current.json'
$failedFile = Join-Path $stateRoot 'failed.json'
$reviewRoot = Join-Path $root 'migration-reviews'
$lock = $null

. (Join-Path $PSScriptRoot 'DevMigrationReview.ps1')

if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-19') {
    throw 'The DEV ready watcher must run as LOCAL SERVICE.'
}
$env:GIT_TERMINAL_PROMPT = '0'
try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'watch.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    return
}

try {
    $lines = @(& 'C:\Program Files\Git\cmd\git.exe' ls-remote --tags --refs $remote 'refs/tags/dev-ready-*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not read DEV ready tags.' }
    $candidates = @($lines | ForEach-Object {
        if ($_ -match '^([0-9a-f]{40})\s+refs/tags/(dev-ready-([0-9]{14})-([0-9a-f]{8}))$' -and
            $Matches[1].StartsWith($Matches[4])) {
            [pscustomobject] @{ Sha = $Matches[1]; Tag = $Matches[2]; Timestamp = $Matches[3] }
        }
    } | Sort-Object Timestamp -Descending)
    if ($candidates.Count -eq 0) { return }
    $ready = $candidates[0]
    if (Test-Path -LiteralPath $currentFile) {
        $current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
        if ($current.Sha -eq $ready.Sha -and $current.Tag -eq $ready.Tag) { return }
    }
    if (Test-Path -LiteralPath $failedFile) {
        $failed = Get-Content -LiteralPath $failedFile -Raw | ConvertFrom-Json
        if ($failed.Tag -eq $ready.Tag -and
            -not (Test-DevMigrationReviewRetryAllowed -Failed $failed -ReviewRoot $reviewRoot)) {
            return
        }
    }
    $log = Join-Path $logsRoot ($ready.Tag + '-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') + '.log')
    Start-Transcript -LiteralPath $log -Force | Out-Null
    try {
        & (Join-Path $root 'automation\Deploy-DevReady.ps1') -Tag $ready.Tag -Sha $ready.Sha
        if (Test-Path -LiteralPath $failedFile) {
            $recordedFailure = Get-Content -LiteralPath $failedFile -Raw | ConvertFrom-Json
            if ($recordedFailure.Tag -eq $ready.Tag -and $recordedFailure.Sha -eq $ready.Sha) {
                Remove-Item -LiteralPath $failedFile -Force
            }
        }
        Write-Output ('DEV DEPLOY SUCCESS ' + $ready.Sha)
    } catch {
        $message = [string] $_.Exception.Message
        Write-Output ('DEV deployment status: ' + $message)
        if ($message -notmatch '^INITIAL_HANDOFF_REQUIRED|^A DEV deployment is already running') {
            @{Tag=$ready.Tag; Sha=$ready.Sha; FailedAtUtc=(Get-Date).ToUniversalTime().ToString('o');
                Reason=$message} | ConvertTo-Json | Set-Content -LiteralPath $failedFile -Encoding UTF8
        }
    } finally {
        Stop-Transcript | Out-Null
    }
} finally {
    if ($lock) { $lock.Dispose() }
}
