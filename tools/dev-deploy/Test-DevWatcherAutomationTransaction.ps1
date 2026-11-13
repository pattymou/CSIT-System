$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevWatcherAutomationTransaction.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('csit-automation-transaction-' + [guid]::NewGuid().ToString('N'))
$names = @('one.ps1','two.ps1')
$passed = 0

function New-Fixture([string] $Name, [switch] $SecondFileAbsent) {
    $root = Join-Path $testRoot $Name
    $source = Join-Path $root 'source'
    $installed = Join-Path $root 'installed'
    $backup = Join-Path $root 'backup'
    foreach ($path in @($source,$installed,$backup)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
    [IO.File]::WriteAllBytes((Join-Path $installed 'one.ps1'), [byte[]](0,1,2,3,13,10,255))
    [IO.File]::WriteAllBytes((Join-Path $installed 'two.ps1'), [byte[]](9,8,7,6,10,0,128))
    if ($SecondFileAbsent) { Remove-Item -LiteralPath (Join-Path $installed 'two.ps1') }
    'approved one' | Set-Content -LiteralPath (Join-Path $source 'one.ps1') -Encoding UTF8
    'approved two' | Set-Content -LiteralPath (Join-Path $source 'two.ps1') -Encoding UTF8
    $newFileAclSddl = (Get-Acl -LiteralPath (Join-Path $installed 'one.ps1')).Sddl
    $records = @(New-DevAutomationFileBackup $installed $backup $names $newFileAclSddl $source)
    Assert-DevAutomationBackup $backup $records
    return [pscustomobject]@{ Source=$source; Installed=$installed; Backup=$backup; Records=$records }
}

function Assert-Originals($Fixture) {
    foreach ($record in $Fixture.Records) {
        $path = Join-Path $Fixture.Installed ([string]$record.Name)
        if (-not [bool]$record.Exists) {
            if (Test-Path -LiteralPath $path) {
                throw "Previously absent file was not removed: $($record.Name)"
            }
            continue
        }
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

    $changedSource = New-Fixture 'changed-source'
    Add-Content -LiteralPath (Join-Path $changedSource.Source 'one.ps1') `
        -Value '# changed after approved SHA capture'
    Assert-ExpectedFailure {
        Install-DevAutomationFiles $changedSource.Source $changedSource.Installed `
            $changedSource.Backup $changedSource.Records
    } '*Approved automation source changed after backup*'
    Assert-Originals $changedSource
    $passed++; Write-Output 'PASS approved_source_sha_change_rejected'

    $restore = New-Fixture 'restore'
    $null = Install-DevAutomationFiles $restore.Source $restore.Installed `
        $restore.Backup $restore.Records
    Assert-ExpectedFailure {
        Restore-DevAutomationFiles $restore.Installed $restore.Backup $restore.Records `
            -InjectRestoreFailure
    } '*Automation restore failures*Injected automation restore failure*'
    $passed++; Write-Output 'PASS restore_failure_reported'

    $absent = New-Fixture 'absent' -SecondFileAbsent
    if (@($absent.Records | Where-Object { -not $_.Exists }).Count -ne 1) {
        throw 'Previously absent file was not represented in the backup manifest.'
    }
    $absentApproved = @(Install-DevAutomationFiles $absent.Source $absent.Installed `
        $absent.Backup $absent.Records
    )
    $absentRecord = @($absent.Records | Where-Object { -not $_.Exists })[0]
    $newPath = Join-Path $absent.Installed 'two.ps1'
    $manifest = Get-Content -LiteralPath (Join-Path $absent.Backup 'installed-files.json') `
        -Raw | ConvertFrom-Json
    $manifestAbsent = @($manifest | ForEach-Object { $_ }) |
        Where-Object { $_.Name -eq 'two.ps1' } | Select-Object -First 1
    if ($absentApproved.Count -ne 2 -or -not $absentRecord.TransactionInstalled -or
        $absentRecord.TransactionSha256 -ne $absentRecord.ApprovedSha256 -or
        $manifestAbsent.Exists -ne $false -or
        [string]::IsNullOrWhiteSpace([string]$manifestAbsent.ApprovedSha256) -or
        (Get-Acl -LiteralPath $newPath).Sddl -ne [string]$absentRecord.AclSddl) {
        throw ('New-file install, ACL, or evidence verification failed: approved=' +
            $absentApproved.Count + '; transactionInstalled=' +
            $absentRecord.TransactionInstalled + '; transactionSha=' +
            $absentRecord.TransactionSha256 + '; approvedSha=' +
            $absentRecord.ApprovedSha256 + '; manifestExists=' +
            $manifestAbsent.Exists + '; manifestApprovedSha=' +
            $manifestAbsent.ApprovedSha256 + '; aclMatch=' +
            ((Get-Acl -LiteralPath $newPath).Sddl -eq [string]$absentRecord.AclSddl))
    }
    Restore-DevAutomationFiles $absent.Installed $absent.Backup $absent.Records
    Assert-Originals $absent
    $passed++; Write-Output 'PASS mixed_new_file_install_acl_evidence_and_restore'

    $mixedFailure = New-Fixture 'mixed-failure' -SecondFileAbsent
    Assert-ExpectedFailure {
        Install-DevAutomationFiles $mixedFailure.Source $mixedFailure.Installed `
            $mixedFailure.Backup $mixedFailure.Records -FailureInjection PartialCopySecond
    } '*second-file automation copy failure*'
    Restore-DevAutomationFiles $mixedFailure.Installed $mixedFailure.Backup `
        $mixedFailure.Records
    Assert-Originals $mixedFailure
    $passed++; Write-Output 'PASS mixed_partial_install_restored'

    $changedNew = New-Fixture 'changed-new' -SecondFileAbsent
    $null = Install-DevAutomationFiles $changedNew.Source $changedNew.Installed `
        $changedNew.Backup $changedNew.Records
    Add-Content -LiteralPath (Join-Path $changedNew.Installed 'two.ps1') `
        -Value '# changed after transaction install'
    Assert-ExpectedFailure {
        Restore-DevAutomationFiles $changedNew.Installed $changedNew.Backup `
            $changedNew.Records
    } '*Automation restore failures*differs from this transaction*approved source and was retained*'
    if (-not (Test-Path -LiteralPath (Join-Path $changedNew.Installed 'two.ps1'))) {
        throw 'A changed newly installed file was deleted during failed restore.'
    }
    $passed++; Write-Output 'PASS changed_new_file_retained_and_restore_failed'

    $appeared = New-Fixture 'appeared' -SecondFileAbsent
    'not created by transaction' | Set-Content -LiteralPath `
        (Join-Path $appeared.Installed 'two.ps1') -Encoding UTF8
    Assert-ExpectedFailure {
        Install-DevAutomationFiles $appeared.Source $appeared.Installed `
            $appeared.Backup $appeared.Records
    } '*Previously absent automation path appeared during update*'
    Assert-ExpectedFailure {
        Restore-DevAutomationFiles $appeared.Installed $appeared.Backup $appeared.Records
    } '*not recorded as this transaction*product and was retained*'
    if ((Get-Content -LiteralPath (Join-Path $appeared.Installed 'two.ps1') -Raw) -notlike
        '*not created by transaction*') {
        throw 'A file not created by the transaction was changed or removed.'
    }
    $passed++; Write-Output 'PASS unrelated_new_path_retained'

    $watchLockPath = Join-Path $testRoot 'watch-conflict.lock'
    $deployLockPath = Join-Path $testRoot 'deploy-conflict.lock'
    $lock = [IO.File]::Open($watchLockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Assert-ExpectedFailure {
            Enter-DevAutomationLocks $watchLockPath $deployLockPath
        } '*DEV watcher is active*'
    } finally { $lock.Dispose() }
    $passed++; Write-Output 'PASS watch_lock_conflict_rejected'

    $lock = [IO.File]::Open($deployLockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Assert-ExpectedFailure {
            Enter-DevAutomationLocks $watchLockPath $deployLockPath
        } '*deployment or maintenance operation is active*'
        $probe = [IO.File]::Open($watchLockPath,[IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $probe.Dispose()
    } finally { $lock.Dispose() }
    $passed++; Write-Output 'PASS deploy_lock_conflict_rejected'

    if ($passed -ne 11) { throw "Unexpected test count: $passed" }
    Write-Output "DEV_WATCHER_AUTOMATION_TRANSACTION_TEST_PASS count=$passed"
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
