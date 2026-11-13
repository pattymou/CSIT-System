$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$stateRoot = Join-Path $root 'state'
$logsRoot = Join-Path $root 'logs'
$remote = 'https://github.com/pattymou/CSIT-System.git'
$currentFile = Join-Path $stateRoot 'current.json'
$failedFile = Join-Path $stateRoot 'failed.json'
$transitionFile = Join-Path $stateRoot 'nas-business-hierarchy-transition.json'
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
    $current = if (Test-Path -LiteralPath $currentFile) {
        Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
    } else { $null }
    $transition = if (Test-Path -LiteralPath $transitionFile) {
        Get-Content -LiteralPath $transitionFile -Raw | ConvertFrom-Json
    } else { $null }
    if ($transition) {
        if ([int]$transition.SchemaVersion -ne 1 -or
            [string]$transition.Status -notin @('AwaitingCompatibleDeployment','RolledBack') -or
            [string]$transition.PreMigrationRuntimeSha -notmatch '^[0-9a-f]{40}$' -or
            [string]$transition.ApplyCommitSha -notmatch '^[0-9a-f]{40}$' -or
            -not $current) {
            throw 'DEV NAS hierarchy transition fence is invalid; automatic recovery is blocked.'
        }
        if ([string]$transition.Status -eq 'RolledBack') {
            if ([string]$current.Sha -ne [string]$transition.PreMigrationRuntimeSha) {
                throw 'Rolled-back DEV hierarchy is not bound to the pre-migration runtime.'
            }
            & (Join-Path $root 'automation\Restart-DevCurrent.ps1')
            return
        }
        if ([string]$current.Sha -notin @(
                [string]$transition.PreMigrationRuntimeSha,
                [string]$transition.ApplyCommitSha)) {
            throw 'Current DEV runtime is unrelated to the pending hierarchy transition.'
        }
    }
    $lines = @(& 'C:\Program Files\Git\cmd\git.exe' ls-remote --tags --refs $remote 'refs/tags/dev-ready-*')
    if ($LASTEXITCODE -ne 0) {
        if ($current -and -not $transition) { & (Join-Path $root 'automation\Restart-DevCurrent.ps1') }
        throw 'Could not read DEV ready tags.'
    }
    $candidates = @($lines | ForEach-Object {
        if ($_ -match '^([0-9a-f]{40})\s+refs/tags/(dev-ready-([0-9]{14})-([0-9a-f]{8}))$' -and
            $Matches[1].StartsWith($Matches[4])) {
            [pscustomobject] @{ Sha = $Matches[1]; Tag = $Matches[2]; Timestamp = $Matches[3] }
        }
    } | Sort-Object Timestamp -Descending)
    if ($candidates.Count -eq 0) {
        if ($current -and -not $transition) { & (Join-Path $root 'automation\Restart-DevCurrent.ps1') }
        return
    }
    $ready = $candidates[0]
    if ($transition -and $ready.Sha -ne [string]$transition.ApplyCommitSha) {
        Write-Output ('DEV NAS hierarchy transition is awaiting compatible commit ' +
            [string]$transition.ApplyCommitSha)
        return
    }
    if ($current) {
        if ($current.Sha -eq $ready.Sha -and $current.Tag -eq $ready.Tag) {
            & (Join-Path $root 'automation\Restart-DevCurrent.ps1')
            if ($transition -and [string]$current.Sha -eq [string]$transition.ApplyCommitSha) {
                Remove-Item -LiteralPath $transitionFile -Force
            }
            return
        }
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
        if ($transition) {
            $deployed = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
            if ([string]$deployed.Sha -ne [string]$transition.ApplyCommitSha) {
                throw 'Compatible hierarchy deployment did not record the approved Apply commit.'
            }
            Remove-Item -LiteralPath $transitionFile -Force
        }
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
