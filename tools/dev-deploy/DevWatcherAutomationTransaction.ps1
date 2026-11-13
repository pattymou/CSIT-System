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
    [string[]] $Names) {
    $filesRoot = Join-Path $BackupRoot 'files'
    $aclRoot = Join-Path $BackupRoot 'file-acls'
    New-Item -ItemType Directory -Path $filesRoot | Out-Null
    New-Item -ItemType Directory -Path $aclRoot | Out-Null
    $records = foreach ($name in $Names) {
        $source = Join-Path $InstalledRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Installed automation file is absent: $name"
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
            Name=$name; Length=$record.Length; Sha256=$record.Sha256; AclSddl=$record.AclSddl
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
    [ValidateSet('None','PartialCopy','HashMismatch')]
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
        $temporary = Join-Path $InstalledRoot ('.update-' + [guid]::NewGuid().ToString('N') + '-' + $name)
        try {
            Copy-Item -LiteralPath $source -Destination $temporary
            $aclSddl = (Get-Content -LiteralPath (Join-Path (Join-Path $BackupRoot 'file-acls') `
                ($name + '.acl.sddl')) -Raw).Trim()
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
            Move-Item -LiteralPath $temporary -Destination $target -Force
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
            $backupItem = Get-Item -LiteralPath $backup
            $backupHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
            if ($backupItem.Length -ne [long]$record.Length -or
                $backupHash -ne [string]$record.Sha256) {
                throw "Backup cannot be restored because its hash changed: $name"
            }
            Copy-Item -LiteralPath $backup -Destination $temporary
            $aclSddl = (Get-Content -LiteralPath (Join-Path (Join-Path $BackupRoot 'file-acls') `
                ($name + '.acl.sddl')) -Raw).Trim()
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
