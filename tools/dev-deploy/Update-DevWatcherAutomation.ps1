[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevWatcherAutomationTransaction.ps1')

$root = 'C:\ProgramData\CSIT-DEV'
$automationRoot = Join-Path $root 'automation'
$reviewRoot = Join-Path $root 'migration-reviews'
$maintenanceRoot = Join-Path $root 'maintenance'
$stateRoot = Join-Path $root 'state'
$currentFile = Join-Path $stateRoot 'current.json'
$transitionFile = Join-Path $stateRoot 'nas-business-hierarchy-transition.json'
$branch = 'feature/km-stage-integration'
$taskName = 'CSIT DEV Ready Watcher'
$names = @('Watch-DevReady.ps1', 'Deploy-DevReady.ps1', 'DevMigrationReview.ps1',
    'Approve-DevMigrationReview.ps1', 'Initialize-DevManagedAssetStorage.ps1',
    'Test-DevNasAccess.ps1', 'Migrate-DevFormalStorageToNas.ps1',
    'Set-DevNasCredential.ps1', 'Restart-DevCurrent.ps1',
    'Request-DevRuntimeMaintenance.ps1', 'Request-DevM2MTokenVerification.ps1',
    'Request-DevKmOutboxDispatch.ps1', 'Request-DevKmOutboxRetry.ps1')
$watchLock = $null
$deployLock = $null
$backupRoot = $null
$originalRecords = $null
$taskBeforeXml = $null
$automationRootSddl = $null
$reviewRootSddl = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Only a local Administrator may update DEV watcher automation.'
    }
}

function Get-ProductionPid {
    $pids = @(Get-NetTCPConnection -State Listen -LocalPort 5010 -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($pids.Count -ne 1) { throw 'Production PID guard failed.' }
    return [int]$pids[0]
}

function Get-TaskSnapshot($Task) {
    return [ordered]@{
        Principal = [ordered]@{
            UserId=[string]$Task.Principal.UserId
            LogonType=[string]$Task.Principal.LogonType
            RunLevel=[string]$Task.Principal.RunLevel
        }
        Actions = @($Task.Actions | ForEach-Object {
            [ordered]@{ Execute=[string]$_.Execute; Arguments=[string]$_.Arguments }
        })
        Triggers = @($Task.Triggers | ForEach-Object {
            [ordered]@{
                Type=[string]$_.CimClass.CimClassName
                Enabled=[bool]$_.Enabled
                StartBoundary=[string]$_.StartBoundary
                RepetitionInterval=[string]$_.Repetition.Interval
                RepetitionDuration=[string]$_.Repetition.Duration
            }
        })
        Settings = [ordered]@{
            MultipleInstances=[string]$Task.Settings.MultipleInstances
            ExecutionTimeLimit=[string]$Task.Settings.ExecutionTimeLimit
            StartWhenAvailable=[bool]$Task.Settings.StartWhenAvailable
        }
    }
}

function Assert-WatcherTask($Task) {
    $sid = ([Security.Principal.NTAccount]::new([string]$Task.Principal.UserId)).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    if ($sid -ne 'S-1-5-19' -or [string]$Task.Principal.LogonType -ne 'ServiceAccount' -or
        [string]$Task.Principal.RunLevel -ne 'Limited') {
        throw 'The DEV watcher task principal is not the approved LOCAL SERVICE identity.'
    }
    if ($Task.Actions.Count -ne 1 -or [string]$Task.Actions[0].Execute -ne 'powershell.exe' -or
        [string]$Task.Actions[0].Arguments -notlike
            '*C:\ProgramData\CSIT-DEV\automation\Watch-DevReady.ps1*') {
        throw 'The DEV watcher task action is not approved.'
    }
    $boot = @($Task.Triggers | Where-Object {
        $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' -and $_.Enabled })
    $periodic = @($Task.Triggers | Where-Object {
        $_.CimClass.CimClassName -eq 'MSFT_TaskTimeTrigger' -and $_.Enabled -and
        [string]$_.Repetition.Interval -eq 'PT1M' })
    if ($Task.Triggers.Count -ne 2 -or $boot.Count -ne 1 -or $periodic.Count -ne 1) {
        throw 'The DEV watcher task triggers are not the approved startup and one-minute schedule.'
    }
}

function New-DirectoryAcl([string] $Sddl) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm($Sddl)
    return $acl
}

function Assert-ProtectedAutomationFileAcl([string] $Path) {
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $ownerSid = ([Security.Principal.NTAccount]::new([string]$acl.Owner)).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $accessSddl = $acl.GetSecurityDescriptorSddlForm(
        [Security.AccessControl.AccessControlSections]::Access)
    $approvedAccessSddl = 'D:PAI(A;;FA;;;SY)(A;;FR;;;LS)(A;;FA;;;BA)'
    if ($ownerSid -ne 'S-1-5-32-544' -or -not $acl.AreAccessRulesProtected -or
        $accessSddl -ne $approvedAccessSddl) {
        throw "The protected automation file ACL is not approved: $Path"
    }
    return $acl.Sddl
}

function Assert-ApprovedAutomationSources([string] $Commit, [string[]] $Names) {
    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name) -or
            [IO.Path]::GetFileName($name) -ne $name) {
            throw "The approved automation whitelist contains an invalid name: $name"
        }
        $relativePath = 'tools/dev-deploy/' + $name
        $treeEntry = @(git ls-tree $Commit -- $relativePath)
        if ($LASTEXITCODE -ne 0 -or $treeEntry.Count -ne 1 -or
            $treeEntry[0] -notmatch '^100644 blob ([0-9a-f]{40})\s') {
            throw "The approved commit does not contain a regular automation source: $name"
        }
        $approvedBlob = $Matches[1]
        $sourcePath = Join-Path $PSScriptRoot $name
        $workingBlob = (git hash-object -- $sourcePath).Trim()
        if ($LASTEXITCODE -ne 0 -or $workingBlob -ne $approvedBlob) {
            throw "Automation source does not match the approved commit: $name"
        }
    }
}

function Write-EvidenceIntegrity([string] $Directory) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $records = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File |
        Where-Object Name -ne 'evidence-integrity.json' | ForEach-Object {
            [ordered]@{
                Path=$_.FullName.Substring($prefix.Length)
                Length=[long]$_.Length
                Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        } | Sort-Object Path)
    $records | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Directory 'evidence-integrity.json') -Encoding UTF8
}

Assert-Administrator
$productionBefore = Get-ProductionPid
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
Assert-ApprovedAutomationSources $sha $names
if (Test-Path -LiteralPath $transitionFile) {
    throw 'DEV watcher automation cannot be updated during a NAS hierarchy transition.'
}
foreach ($path in @($automationRoot,$reviewRoot,$maintenanceRoot,$currentFile)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required DEV path is absent: $path" }
}
$runtimeBefore = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
Assert-WatcherTask $task
$taskBeforeXml = Export-ScheduledTask -TaskName $taskName
$taskSnapshot = Get-TaskSnapshot $task
$automationRootSddl = (Get-Acl -LiteralPath $automationRoot).Sddl
$reviewRootSddl = (Get-Acl -LiteralPath $reviewRoot).Sddl
$newFileAclSddl = Assert-ProtectedAutomationFileAcl `
    (Join-Path $automationRoot 'Watch-DevReady.ps1')

$locks = Enter-DevAutomationLocks (Join-Path $stateRoot 'watch.lock') `
    (Join-Path $stateRoot 'deploy.lock')
$watchLock = $locks.Watch
$deployLock = $locks.Deploy

try {
    if ((Get-ProductionPid) -ne $productionBefore) {
        throw 'Production PID changed before automation backup.'
    }
    if (Test-Path -LiteralPath $transitionFile) {
        throw 'A NAS hierarchy transition appeared after maintenance locks were acquired.'
    }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $backupRoot = Join-Path $maintenanceRoot "automation-update-$stamp-$($sha.Substring(0,8))"
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    & icacls.exe $backupRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' `
        '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Automation backup ACL protection failed.' }

    $taskBeforeXml | Set-Content -LiteralPath (Join-Path $backupRoot 'task.before.xml') -Encoding Unicode
    $taskSnapshot | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $backupRoot 'task.before.json') -Encoding UTF8
    [ordered]@{ AutomationRoot=$automationRootSddl; ReviewRoot=$reviewRootSddl } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'directory-acls.before.json') -Encoding UTF8
    [ordered]@{
        SourceCommit=$sha; Branch=$branch; ProductionPid=$productionBefore
        RuntimeSha=[string]$runtimeBefore.Sha; RuntimePid=[int]$runtimeBefore.Pid
        StartedAtUtc=(Get-Date).ToUniversalTime().ToString('o'); Status='BackingUp'
        ScriptNames=$names
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $backupRoot 'update-summary.json') -Encoding UTF8

    $originalRecords = @(New-DevAutomationFileBackup $automationRoot $backupRoot $names `
        $newFileAclSddl $PSScriptRoot)
    Assert-DevAutomationBackup $backupRoot $originalRecords
    $approvedRecords = @(Install-DevAutomationFiles $PSScriptRoot $automationRoot `
        $backupRoot $originalRecords)
    foreach ($record in $approvedRecords) {
        $source = Join-Path $PSScriptRoot ([string]$record.Name)
        $target = Join-Path $automationRoot ([string]$record.Name)
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            throw "Approved automation SHA256 comparison failed: $($record.Name)"
        }
    }
    if ((Get-Acl -LiteralPath $automationRoot).Sddl -ne $automationRootSddl -or
        (Get-Acl -LiteralPath $reviewRoot).Sddl -ne $reviewRootSddl) {
        throw 'Protected DEV directory ACL changed during automation update.'
    }
    $taskAfter = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Assert-WatcherTask $taskAfter
    if ((Export-ScheduledTask -TaskName $taskName) -ne $taskBeforeXml) {
        throw 'The DEV watcher task definition changed during automation update.'
    }
    $runtimeAfter = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
    if ([string]$runtimeAfter.Sha -ne [string]$runtimeBefore.Sha -or
        [int]$runtimeAfter.Pid -ne [int]$runtimeBefore.Pid) {
        throw 'DEV runtime state changed during automation update.'
    }
    if ((Get-ProductionPid) -ne $productionBefore) {
        throw 'Production PID changed during DEV watcher automation update.'
    }
    [ordered]@{
        SourceCommit=$sha; Branch=$branch; ProductionPid=$productionBefore
        RuntimeSha=[string]$runtimeBefore.Sha; RuntimePid=[int]$runtimeBefore.Pid
        CompletedAtUtc=(Get-Date).ToUniversalTime().ToString('o')
        Status='Updated'; InstalledFiles=$approvedRecords; OriginalFiles=$originalRecords
        Task=$taskSnapshot
    } | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $backupRoot 'update-summary.json') -Encoding UTF8
    Write-EvidenceIntegrity $backupRoot
    Write-Output ('DEV_WATCHER_AUTOMATION_UPDATED sha=' + $sha + ' task=' + $taskName +
        ' evidence=' + $backupRoot)
} catch {
    $failure = $_
    $recoveryFailures = [Collections.Generic.List[string]]::new()
    if ($backupRoot -and $originalRecords) {
        try { Restore-DevAutomationFiles $automationRoot $backupRoot $originalRecords }
        catch { $recoveryFailures.Add($_.Exception.Message) }
        try { Set-Acl -LiteralPath $automationRoot -AclObject (New-DirectoryAcl $automationRootSddl) }
        catch { $recoveryFailures.Add('Automation root ACL: ' + $_.Exception.Message) }
        try { Set-Acl -LiteralPath $reviewRoot -AclObject (New-DirectoryAcl $reviewRootSddl) }
        catch { $recoveryFailures.Add('Review root ACL: ' + $_.Exception.Message) }
        try {
            Register-ScheduledTask -TaskName $taskName -Xml $taskBeforeXml -Force | Out-Null
            Assert-WatcherTask (Get-ScheduledTask -TaskName $taskName -ErrorAction Stop)
            if ((Export-ScheduledTask -TaskName $taskName) -ne $taskBeforeXml) {
                throw 'Restored watcher task does not match its saved XML definition.'
            }
        } catch { $recoveryFailures.Add('Watcher task: ' + $_.Exception.Message) }
        try {
            if ((Get-Acl -LiteralPath $automationRoot).Sddl -ne $automationRootSddl -or
                (Get-Acl -LiteralPath $reviewRoot).Sddl -ne $reviewRootSddl) {
                throw 'Restored protected directory ACL verification failed.'
            }
        } catch { $recoveryFailures.Add('Directory ACL verification: ' + $_.Exception.Message) }
        try { Assert-DevAutomationBackup $backupRoot $originalRecords }
        catch { $recoveryFailures.Add('Backup verification: ' + $_.Exception.Message) }
        $status = if ($recoveryFailures.Count) { 'RollbackFailed' } else { 'RolledBack' }
        [ordered]@{
            SourceCommit=$sha; Status=$status; FailedAtUtc=(Get-Date).ToUniversalTime().ToString('o')
            Failure=$failure.Exception.Message; RecoveryFailures=@($recoveryFailures)
            OriginalFiles=$originalRecords; Task=$taskSnapshot
        } | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $backupRoot 'update-summary.json') -Encoding UTF8
        Write-EvidenceIntegrity $backupRoot
    } elseif ($backupRoot) {
        [ordered]@{
            SourceCommit=$sha; Status='BackupFailed'; FailedAtUtc=(Get-Date).ToUniversalTime().ToString('o')
            Failure=$failure.Exception.Message; RecoveryFailures=@()
        } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $backupRoot 'update-summary.json') -Encoding UTF8
        Write-EvidenceIntegrity $backupRoot
    }
    if ($recoveryFailures.Count) {
        throw "Automation update failed: $($failure.Exception.Message); recovery failures: $($recoveryFailures -join '; ')"
    }
    throw $failure
} finally {
    if ($deployLock) { $deployLock.Dispose(); $deployLock = $null }
    if ($watchLock) { $watchLock.Dispose(); $watchLock = $null }
}
