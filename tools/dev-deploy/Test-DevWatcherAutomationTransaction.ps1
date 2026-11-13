$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevWatcherAutomationTransaction.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('csit-automation-transaction-' + [guid]::NewGuid().ToString('N'))
$names = @('one.ps1','two.ps1')
$passed = 0

function New-Fixture([string] $Name) {
    $root = Join-Path $testRoot $Name
    $source = Join-Path $root 'source'
    $installed = Join-Path $root 'installed'
    $backup = Join-Path $root 'backup'
    foreach ($path in @($source,$installed,$backup)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
    [IO.File]::WriteAllBytes((Join-Path $installed 'one.ps1'), [byte[]](0,1,2,3,13,10,255))
    [IO.File]::WriteAllBytes((Join-Path $installed 'two.ps1'), [byte[]](9,8,7,6,10,0,128))
    'approved one' | Set-Content -LiteralPath (Join-Path $source 'one.ps1') -Encoding UTF8
    'approved two' | Set-Content -LiteralPath (Join-Path $source 'two.ps1') -Encoding UTF8
    $records = @(New-DevAutomationFileBackup $installed $backup $names)
    Assert-DevAutomationBackup $backup $records
    return [pscustomobject]@{ Source=$source; Installed=$installed; Backup=$backup; Records=$records }
}

function Assert-Originals($Fixture) {
    foreach ($record in $Fixture.Records) {
        $path = Join-Path $Fixture.Installed ([string]$record.Name)
        if ((Get-Item -LiteralPath $path).Length -ne [long]$record.Length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$record.Sha256 -or
            (Get-Acl -LiteralPath $path).Sddl -ne [string]$record.AclSddl) {
            throw "Original was not restored: $($record.Name)"
        }
    }
}

function Assert-ExpectedFailure([scriptblock] $Action, [string] $Pattern) {
    try { & $Action; throw 'Expected failure was not raised.' }
    catch {
        if ($_.Exception.Message -eq 'Expected failure was not raised.') { throw }
        if ($_.Exception.Message -notlike $Pattern) {
            throw "Unexpected failure: $($_.Exception.Message)"
        }
    }
}

try {
    $success = New-Fixture 'success'
    $approved = @(Install-DevAutomationFiles $success.Source $success.Installed `
        $success.Backup $success.Records)
    if ($approved.Count -ne 2) { throw 'Successful install did not verify every file.' }
    Restore-DevAutomationFiles $success.Installed $success.Backup $success.Records
    Assert-Originals $success
    $passed++; Write-Output 'PASS backup_update_restore_round_trip'

    $partial = New-Fixture 'partial'
    Assert-ExpectedFailure {
        Install-DevAutomationFiles $partial.Source $partial.Installed $partial.Backup `
            $partial.Records -FailureInjection PartialCopy
    } '*partial automation copy failure*'
    Restore-DevAutomationFiles $partial.Installed $partial.Backup $partial.Records
    Assert-Originals $partial
    $passed++; Write-Output 'PASS partial_copy_failure_restored'

    $hash = New-Fixture 'hash'
    Assert-ExpectedFailure {
        Install-DevAutomationFiles $hash.Source $hash.Installed $hash.Backup `
            $hash.Records -FailureInjection HashMismatch
    } '*Staged automation verification failed*'
    Restore-DevAutomationFiles $hash.Installed $hash.Backup $hash.Records
    Assert-Originals $hash
    $passed++; Write-Output 'PASS sha_mismatch_rejected_and_restored'

    $restore = New-Fixture 'restore'
    $null = Install-DevAutomationFiles $restore.Source $restore.Installed `
        $restore.Backup $restore.Records
    Assert-ExpectedFailure {
        Restore-DevAutomationFiles $restore.Installed $restore.Backup $restore.Records `
            -InjectRestoreFailure
    } '*Automation restore failures*Injected automation restore failure*'
    $passed++; Write-Output 'PASS restore_failure_reported'

    $lockPath = Join-Path $testRoot 'conflict.lock'
    $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Assert-ExpectedFailure {
            $second = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $second.Dispose()
        } '*'
    } finally { $lock.Dispose() }
    $passed++; Write-Output 'PASS exclusive_lock_conflict_rejected'

    if ($passed -ne 5) { throw "Unexpected test count: $passed" }
    Write-Output "DEV_WATCHER_AUTOMATION_TRANSACTION_TEST_PASS count=$passed"
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
