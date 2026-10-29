$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$automationRoot = Join-Path $root 'automation'
$reviewRoot = Join-Path $root 'migration-reviews'
$branch = 'feature/km-stage-integration'
$taskName = 'CSIT DEV Ready Watcher'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may update DEV watcher automation.'
}
$productionBefore = @(Get-NetTCPConnection -State Listen -LocalPort 5010 |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($productionBefore.Count -ne 1) { throw 'Production PID guard failed.' }
$currentBranch = (git branch --show-current).Trim()
$workingChanges = @(git status --porcelain)
if ($currentBranch -ne $branch -or $workingChanges.Count -gt 0) {
    throw 'DEV watcher automation must be installed from the clean designated branch.'
}
$sha = (git rev-parse HEAD).Trim()
$remoteSha = ((git ls-remote origin "refs/heads/$branch") -split '\s+')[0]
if ($sha -notmatch '^[0-9a-f]{40}$' -or $remoteSha -ne $sha) {
    throw 'DEV watcher automation source is not the pushed branch head.'
}
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
$taskSid = ([Security.Principal.NTAccount]::new([string] $task.Principal.UserId)).Translate(
    [Security.Principal.SecurityIdentifier]).Value
if ($taskSid -ne 'S-1-5-19') {
    throw 'The DEV watcher task principal is not LOCAL SERVICE.'
}
$updateLock = $null
try {
    $updateLock = [IO.File]::Open((Join-Path $root 'state\watch.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    throw 'The DEV watcher is active; automation was not updated.'
}
try {
New-Item -ItemType Directory -Path $automationRoot -Force | Out-Null
New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
foreach ($restricted in @($automationRoot, $reviewRoot)) {
    & icacls.exe $restricted /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' `
        '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'DEV restricted directory ACL update failed.' }
}
$names = @('Watch-DevReady.ps1', 'Deploy-DevReady.ps1', 'DevMigrationReview.ps1',
    'Approve-DevMigrationReview.ps1', 'Initialize-DevManagedAssetStorage.ps1')
foreach ($name in $names) {
    $source = Join-Path $PSScriptRoot $name
    $target = Join-Path $automationRoot $name
    Copy-Item -LiteralPath $source -Destination $target -Force
    & icacls.exe $target /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0 -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash) {
        throw 'DEV automation script verification failed.'
    }
}
if (@(Get-NetTCPConnection -State Listen -LocalPort 5010 |
        Select-Object -ExpandProperty OwningProcess -Unique)[0] -ne $productionBefore[0]) {
    throw 'Production PID changed during DEV watcher automation update.'
}
Write-Output ('DEV_WATCHER_AUTOMATION_UPDATED sha=' + $sha + ' task=' + $taskName)
} finally {
    if ($updateLock) { $updateLock.Dispose() }
}
