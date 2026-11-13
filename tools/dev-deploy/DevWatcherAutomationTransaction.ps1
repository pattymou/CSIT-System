function Get-DevAutomationFileRecord([string] $Path, [string] $Name) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Automation file is not a regular file: $Path"
    }
    $acl = Get-Acl -LiteralPath $Path
    return [pscustomobject]@{
        Name = $Name
        Length = [long]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        AclSddl = $acl.Sddl
        Acl = $acl
    }
}

function New-DevAutomationFileAcl([string] $Sddl) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetSecurityDescriptorSddlForm($Sddl)
    return $acl
}

function New-DevAutomationFileBackup(
    [string] $InstalledRoot,
    [string] $BackupRoot,
    [string[]] $Names,
    [string] $NewFileAclSddl,
    [string] $ApprovedSourceRoot) {
    $filesRoot = Join-Path $BackupRoot 'files'
    $aclRoot = Join-Path $BackupRoot 'file-acls'
    New-Item -ItemType Directory -Path $filesRoot | Out-Null
    New-Item -ItemType Directory -Path $aclRoot | Out-Null
    if (@($Names | Select-Object -Unique).Count -ne $Names.Count) {
        throw 'The approved automation whitelist contains duplicate names.'
    }
    $records = foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name) -or
            [IO.Path]::GetFileName($name) -ne $name) {
            throw "The approved automation whitelist contains an invalid name: $name"
        }
        $approvedSource = Join-Path $ApprovedSourceRoot $name
        if (-not (Test-Path -LiteralPath $approvedSource -PathType Leaf)) {
            throw "Approved automation source is absent: $name"
        }
        $approvedRecord = Get-DevAutomationFileRecord $approvedSource $name
        $source = Join-Path $InstalledRoot $name
        if (-not (Test-Path -LiteralPath $source)) {
            if ([string]::IsNullOrWhiteSpace($NewFileAclSddl)) {
                throw "No approved ACL is available for new automation file: $name"
            }
            [pscustomobject]@{
                Name=$name; Exists=$false; Length=0; Sha256=$null; AclSddl=$NewFileAclSddl
                ApprovedLength=$approvedRecord.Length; ApprovedSha256=$approvedRecord.Sha256
                TransactionInstalled=$false; TransactionSha256=$null
            }
            continue
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Installed automation path is not a file: $name"
        }
        $record = Get-DevAutomationFileRecord $source $name
        $backup = Join-Path $filesRoot $name
        Copy-Item -LiteralPath $source -Destination $backup
        $record.AclSddl | Set-Content -LiteralPath (Join-Path $aclRoot ($name + '.acl.sddl')) `
            -Encoding ASCII
        $copied = Get-DevAutomationFileRecord $backup $name
        if ($copied.Length -ne $record.Length -or $copied.Sha256 -ne $record.Sha256) {
            throw "Automation backup verification failed: $name"
        }
        [pscustomobject]@{
            Name=$name; Exists=$true; Length=$record.Length; Sha256=$record.Sha256;
            AclSddl=$record.AclSddl; ApprovedLength=$approvedRecord.Length;
            ApprovedSha256=$approvedRecord.Sha256; TransactionInstalled=$false;
            TransactionSha256=$null
        }
    }
    @($records) | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $BackupRoot 'installed-files.json') -Encoding UTF8
    return @($records)
}

function Assert-DevAutomationBackup(
    [string] $BackupRoot,
    $Records) {
    foreach ($record in $Records) {
        $backup = Join-Path (Join-Path $BackupRoot 'files') ([string]$record.Name)
        $aclBackup = Join-Path (Join-Path $BackupRoot 'file-acls') `
            (([string]$record.Name) + '.acl.sddl')
        if (-not [bool]$record.Exists) {
            if (Test-Path -LiteralPath $backup) {
                throw "Unexpected backup exists for a previously absent file: $($record.Name)"
            }
            continue
        }
        if (-not (Test-Path -LiteralPath $backup -PathType Leaf) -or
            -not (Test-Path -LiteralPath $aclBackup -PathType Leaf)) {
            throw "Automation backup is incomplete: $($record.Name)"
        }
        $item = Get-Item -LiteralPath $backup
        $hash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
        if ($item.Length -ne [long]$record.Length -or $hash -ne [string]$record.Sha256) {
            throw "Automation backup changed after capture: $($record.Name)"
        }
        $aclSddl = (Get-Content -LiteralPath $aclBackup -Raw).Trim()
        if ($aclSddl -ne [string]$record.AclSddl) {
            throw "Automation ACL backup verification failed: $($record.Name)"
        }
    }
}

function Install-DevAutomationFiles(
    [string] $ApprovedSourceRoot,
    [string] $InstalledRoot,
    [string] $BackupRoot,
    $OriginalRecords,
    [ValidateSet('None','PartialCopy','PartialCopySecond','HashMismatch')]
    [string] $FailureInjection = 'None') {
    $approved = [Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($record in $OriginalRecords) {
        $name = [string]$record.Name
        $source = Join-Path $ApprovedSourceRoot $name
        $target = Join-Path $InstalledRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Approved automation source is absent: $name"
        }
        $sourceRecord = Get-DevAutomationFileRecord $source $name
        if ($sourceRecord.Length -ne [long]$record.ApprovedLength -or
            $sourceRecord.Sha256 -ne [string]$record.ApprovedSha256) {
            throw "Approved automation source changed after backup: $name"
        }
        $temporary = Join-Path $InstalledRoot ('.update-' + [guid]::NewGuid().ToString('N') + '-' + $name)
        try {
            Copy-Item -LiteralPath $source -Destination $temporary
            $aclSddl = [string]$record.AclSddl
            $acl = New-DevAutomationFileAcl $aclSddl
            Set-Acl -LiteralPath $temporary -AclObject $acl
            if ($FailureInjection -eq 'HashMismatch' -and $index -eq 0) {
                Add-Content -LiteralPath $temporary -Value '# injected hash mismatch'
            }
            $temporaryRecord = Get-DevAutomationFileRecord $temporary $name
            if ($temporaryRecord.Length -ne $sourceRecord.Length -or
                $temporaryRecord.Sha256 -ne $sourceRecord.Sha256 -or
                $temporaryRecord.AclSddl -ne [string]$record.AclSddl) {
                throw "Staged automation verification failed: $name"
            }
            if (-not [bool]$record.Exists) {
                if (Test-Path -LiteralPath $target) {
                    throw "Previously absent automation path appeared during update: $name"
                }
                [IO.File]::Move($temporary, $target)
                $record.TransactionInstalled = $true
                $record.TransactionSha256 = [string]$sourceRecord.Sha256
            } else {
                Move-Item -LiteralPath $temporary -Destination $target -Force
            }
            Set-Acl -LiteralPath $target -AclObject $acl
            $installed = Get-DevAutomationFileRecord $target $name
            if ($installed.Length -ne $sourceRecord.Length -or
                $installed.Sha256 -ne $sourceRecord.Sha256 -or
                $installed.AclSddl -ne [string]$record.AclSddl) {
                throw "Installed automation verification failed: $name"
            }
            $approved.Add([pscustomobject]@{
                Name=$name; Length=$installed.Length; Sha256=$installed.Sha256;
                AclSddl=$installed.AclSddl
            })
            if ($FailureInjection -eq 'PartialCopy' -and $index -eq 0) {
                throw 'Injected partial automation copy failure.'
            }
            if ($FailureInjection -eq 'PartialCopySecond' -and $index -eq 1) {
                throw 'Injected second-file automation copy failure.'
            }
        } finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
        $index++
    }
    return @($approved)
}

function Restore-DevAutomationFiles(
    [string] $InstalledRoot,
    [string] $BackupRoot,
    $OriginalRecords,
    [switch] $InjectRestoreFailure) {
    $failures = [Collections.Generic.List[string]]::new()
    $index = 0
    foreach ($record in $OriginalRecords) {
        $name = [string]$record.Name
        $backup = Join-Path (Join-Path $BackupRoot 'files') $name
        $target = Join-Path $InstalledRoot $name
        $temporary = Join-Path $InstalledRoot ('.restore-' + [guid]::NewGuid().ToString('N') + '-' + $name)
        try {
            if ($InjectRestoreFailure -and $index -eq 0) {
                throw 'Injected automation restore failure.'
            }
            if (-not [bool]$record.Exists) {
                if (-not (Test-Path -LiteralPath $target)) { continue }
                if (-not [bool]$record.TransactionInstalled -or
                    [string]::IsNullOrWhiteSpace([string]$record.TransactionSha256)) {
                    throw "New automation path was not recorded as this transaction's product and was retained: $name"
                }
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    throw "New automation path is not a regular transaction file and was retained: $name"
                }
                $targetRecord = Get-DevAutomationFileRecord $target $name
                if ($targetRecord.Length -ne [long]$record.ApprovedLength -or
                    $targetRecord.Sha256 -ne [string]$record.TransactionSha256 -or
                    [string]$record.TransactionSha256 -ne [string]$record.ApprovedSha256) {
                    throw "New automation file differs from this transaction's approved source and was retained: $name"
                }
                Remove-Item -LiteralPath $target -Force
                continue
            }
            $backupItem = Get-Item -LiteralPath $backup
            $backupHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
            if ($backupItem.Length -ne [long]$record.Length -or
                $backupHash -ne [string]$record.Sha256) {
                throw "Backup cannot be restored because its hash changed: $name"
            }
            Copy-Item -LiteralPath $backup -Destination $temporary
            $aclSddl = [string]$record.AclSddl
            $acl = New-DevAutomationFileAcl $aclSddl
            Set-Acl -LiteralPath $temporary -AclObject $acl
            Move-Item -LiteralPath $temporary -Destination $target -Force
            Set-Acl -LiteralPath $target -AclObject $acl
            $restored = Get-DevAutomationFileRecord $target $name
            if ($restored.Length -ne [long]$record.Length -or
                $restored.Sha256 -ne [string]$record.Sha256 -or
                $restored.AclSddl -ne [string]$record.AclSddl) {
                throw "Restored automation verification failed: $name"
            }
        } catch {
            $failures.Add("$name`: $($_.Exception.Message)")
        } finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            }
        }
        $index++
    }
    if ($failures.Count) {
        throw ('Automation restore failures: ' + ($failures -join '; '))
    }
}

function Enter-DevAutomationLocks([string] $WatchLockPath, [string] $DeployLockPath) {
    $watchLock = $null
    try {
        try {
            $watchLock = [IO.File]::Open($WatchLockPath, [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            throw 'The DEV watcher is active; automation was not updated.'
        }
        try {
            $deployLock = [IO.File]::Open($DeployLockPath, [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            throw 'A DEV deployment or maintenance operation is active; automation was not updated.'
        }
        return [pscustomobject]@{ Watch=$watchLock; Deploy=$deployLock }
    } catch {
        if ($watchLock) { $watchLock.Dispose() }
        throw
    }
}
