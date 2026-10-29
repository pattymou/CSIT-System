[CmdletBinding(DefaultParameterSetName = 'Audit')]
param(
    [Parameter(ParameterSetName = 'Apply')]
    [switch] $Apply,
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidateNotNullOrEmpty()]
    [string[]] $ApprovedEquipmentIds,
    [Parameter(Mandatory = $true, ParameterSetName = 'Restore')]
    [ValidateNotNullOrEmpty()]
    [string] $RestoreBackup
)

$ErrorActionPreference = 'Stop'
$devRoot = 'C:\ProgramData\CSIT-DEV'
$secretFile = Join-Path $devRoot 'secrets\runtime.json'
$evidenceRoot = Join-Path $devRoot 'maintenance'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$expectedDatabase = 'sit_department_system_dev'
$expectedUser = 'csit_runtime_dev'
$expectedStorageRoot = 'D:\CSIT_Dev_Data\Assets\equipment'
$localServiceSid = 'S-1-5-19'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Only a local Administrator may initialize DEV managed storage ACLs.'
    }
}

function Assert-ProductionGuard {
    $production = @(Get-NetTCPConnection -State Listen -LocalPort 5010 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($production.Count -ne 1) { throw 'Production PID guard failed.' }
    return [int] $production[0]
}

function Get-DevConnection {
    if (-not (Test-Path -LiteralPath $secretFile) -or -not (Test-Path -LiteralPath $psql)) {
        throw 'DEV runtime configuration or PostgreSQL client is unavailable.'
    }
    $secrets = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
    $raw = $secrets.PSObject.Properties['ConnectionStrings:Postgres'].Value
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString([string] $raw)
    if ([string] $connection['Database'] -ne $expectedDatabase -or
        [string] $connection['Username'] -ne $expectedUser -or
        -not $connection.ContainsKey('Password')) {
        throw 'The configured database is not the authorized CSIT DEV database and role.'
    }
    return $connection
}

function Invoke-DevQuery([Data.Common.DbConnectionStringBuilder] $Connection, [string] $Sql) {
    $env:PGPASSWORD = [string] $Connection['Password']
    try {
        $result = @($Sql | & $psql -X -w -t -A -F '|' -v ON_ERROR_STOP=1 `
            -h ([string] $Connection['Host']) -p ([string] $Connection['Port']) `
            -U ([string] $Connection['Username']) -d ([string] $Connection['Database']) 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('DEV database query failed: ' + ($result -join ' ')) }
        return @($result | ForEach-Object { ([string] $_).Trim() } | Where-Object { $_ })
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Get-ActiveDeletionInventory([Data.Common.DbConnectionStringBuilder] $Connection) {
    $masterRows = @(Invoke-DevQuery $Connection @'
SELECT equipment_id, status, attachment_count, coalesce(last_error_code, '')
FROM equipment_deletion_jobs WHERE status <> 'Completed' ORDER BY equipment_id;
'@)
    $fileRows = @(Invoke-DevQuery $Connection @'
SELECT j.equipment_id, j.apparatus_file_id, j.status, coalesce(j.last_error_code, ''),
       f."FilePath", f."FileName"
FROM equipment_file_deletion_jobs j
LEFT JOIN apparatus_files f ON f."Id" = j.apparatus_file_id
WHERE j.status <> 'Completed' ORDER BY j.equipment_id, j.apparatus_file_id;
'@)
    return [pscustomobject]@{
        Master = @($masterRows | ForEach-Object {
            $parts = $_ -split '\|', 4
            [pscustomobject]@{ EquipmentId=$parts[0]; Status=$parts[1];
                AttachmentCount=[int]$parts[2]; ErrorCode=$parts[3] }
        })
        Files = @($fileRows | ForEach-Object {
            $parts = $_ -split '\|', 6
            [pscustomobject]@{ EquipmentId=$parts[0]; ApparatusFileId=$parts[1];
                Status=$parts[2]; ErrorCode=$parts[3]; FilePath=$parts[4]; FileName=$parts[5] }
        })
    }
}

function Get-StorageInventory([Data.Common.DbConnectionStringBuilder] $Connection) {
    $equipmentRoots = @(Invoke-DevQuery $Connection @'
SELECT value FROM system_options
WHERE category = 'AssetRootPath' AND lower(name) = 'equipment' AND is_enabled
ORDER BY id;
'@)
    if ($equipmentRoots.Count -ne 1) { throw 'DEV must have exactly one enabled AssetRootPath/equipment.' }
    $storageRoot = [IO.Path]::GetFullPath($equipmentRoots[0]).TrimEnd('\')
    if (-not $storageRoot.Equals($expectedStorageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The DEV equipment root is outside the approved scope: $storageRoot"
    }
    if (-not (Test-Path -LiteralPath $storageRoot -PathType Container)) {
        throw 'The DEV equipment root does not exist.'
    }

    $otherRoots = @(Invoke-DevQuery $Connection @'
SELECT name, value FROM system_options
WHERE category = 'AssetRootPath' AND is_enabled AND lower(name) <> 'equipment'
ORDER BY name;
'@ | ForEach-Object {
        $parts = $_ -split '\|', 2
        [pscustomobject]@{ Name=$parts[0]; Path=[IO.Path]::GetFullPath($parts[1]).TrimEnd('\') }
    })
    foreach ($other in $otherRoots) {
        if ($other.Path.Equals($storageRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $other.Path.StartsWith($storageRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $storageRoot.StartsWith($other.Path + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "The equipment root overlaps another enabled AssetRootPath: $($other.Name)"
        }
    }

    $items = @((Get-Item -LiteralPath $storageRoot)) +
        @(Get-ChildItem -LiteralPath $storageRoot -Recurse -Force)
    $protected = @($items | Where-Object {
        (Get-Acl -LiteralPath $_.FullName).AreAccessRulesProtected
    } | Select-Object -ExpandProperty FullName)
    return [pscustomobject]@{
        Root = $storageRoot
        OtherRoots = $otherRoots
        ItemCount = $items.Count
        ProtectedPaths = $protected
        RootAcl = Get-Acl -LiteralPath $storageRoot
    }
}

function Test-LocalServiceModify([Security.AccessControl.FileSystemSecurity] $Acl) {
    return @($Acl.Access | Where-Object {
        $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $localServiceSid -and
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq
            [Security.AccessControl.FileSystemRights]::Modify
    }).Count -gt 0
}

function Assert-ApprovedActiveSet($Inventory) {
    $active = @($Inventory.Master.EquipmentId | Sort-Object -Unique)
    $approved = @($ApprovedEquipmentIds | ForEach-Object { $_.Trim() } |
        Where-Object { $_ } | Sort-Object -Unique)
    if (($active -join "`n") -cne ($approved -join "`n")) {
        throw ('ApprovedEquipmentIds must exactly match every active deletion job. Active=' +
            ($active -join ',') + '; Approved=' + ($approved -join ','))
    }
}

Assert-Administrator
$productionPid = Assert-ProductionGuard
$connection = Get-DevConnection
$deletions = Get-ActiveDeletionInventory $connection
$storage = Get-StorageInventory $connection

if ($PSCmdlet.ParameterSetName -eq 'Restore') {
    $maintenance = [IO.Path]::GetFullPath($evidenceRoot).TrimEnd('\') + '\'
    $backupPath = [IO.Path]::GetFullPath($RestoreBackup)
    if (-not $backupPath.StartsWith($maintenance, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ACL backup must be inside the DEV maintenance directory.'
    }
    if ($deletions.Master.Count -ne 0) {
        throw 'ACL restore is blocked while equipment deletion jobs are active.'
    }
    $backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    if (-not ([string] $backup.StorageRoot).Equals($storage.Root,
        [StringComparison]::OrdinalIgnoreCase) -or [string]::IsNullOrWhiteSpace($backup.RootSddl)) {
        throw 'ACL backup does not match the current DEV equipment root.'
    }
    $acl = Get-Acl -LiteralPath $storage.Root
    $acl.SetSecurityDescriptorSddlForm([string] $backup.RootSddl)
    Set-Acl -LiteralPath $storage.Root -AclObject $acl
    Write-Output ('DEV_MANAGED_STORAGE_ACL_RESTORED root=' + $storage.Root)
    exit 0
}

$report = [ordered]@{
    CapturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Database = $expectedDatabase
    RuntimeIdentity = 'NT AUTHORITY\LOCAL SERVICE'
    StorageRoot = $storage.Root
    OtherAssetRoots = $storage.OtherRoots
    ItemCount = $storage.ItemCount
    ProtectedPaths = $storage.ProtectedPaths
    HasLocalServiceModify = Test-LocalServiceModify $storage.RootAcl
    ActiveDeletionJobs = $deletions.Master
    ActiveFileDeletionJobs = $deletions.Files
    RootSddl = $storage.RootAcl.Sddl
    ProductionPid = $productionPid
}

if (-not $Apply) {
    $report | ConvertTo-Json -Depth 8
    Write-Output 'DEV_MANAGED_STORAGE_ACL_AUDIT_ONLY'
    exit 0
}

Assert-ApprovedActiveSet $deletions
if ($storage.ProtectedPaths.Count -ne 0) {
    throw 'Protected ACL descendants require separate review; no ACL was changed.'
}
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$backupFile = Join-Path $evidenceRoot ('managed-equipment-storage-acl-before-' +
    (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') + '.json')
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupFile -Encoding UTF8

if (-not (Test-LocalServiceModify $storage.RootAcl)) {
    $sid = [Security.Principal.SecurityIdentifier]::new($localServiceSid)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,
        [Security.AccessControl.FileSystemRights]::Modify,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $storage.RootAcl.AddAccessRule($rule) | Out-Null
    Set-Acl -LiteralPath $storage.Root -AclObject $storage.RootAcl
}

$verified = Get-Acl -LiteralPath $storage.Root
if (-not (Test-LocalServiceModify $verified)) {
    throw 'LOCAL SERVICE Modify verification failed after the ACL update.'
}
if ((Assert-ProductionGuard) -ne $productionPid) { throw 'Production PID changed during DEV ACL initialization.' }
Write-Output ('DEV_MANAGED_STORAGE_ACL_APPLIED root=' + $storage.Root + ' backup=' + $backupFile)
