[CmdletBinding(DefaultParameterSetName = 'Audit')]
param(
    [Parameter(Mandatory = $true)]
    [string] $TechnicalRoot,
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [switch] $Apply,
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ApprovedApplyCommitSha,
    [Parameter(ParameterSetName = 'Apply')]
    [string] $ReentryEvidenceDirectory,
    [Parameter(ParameterSetName = 'Apply')]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ApprovedPriorApplyCommitSha,
    [Parameter(ParameterSetName = 'Apply')]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ApprovedPriorManifestSha256,
    [switch] $LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'DevNasHierarchyEvidence.ps1')
$devRoot = 'C:\ProgramData\CSIT-DEV'
$runtimeFile = Join-Path $devRoot 'secrets\runtime.json'
$currentFile = Join-Path $devRoot 'state\current.json'
$lockPath = Join-Path $devRoot 'state\deploy.lock'
$watchLockPath = Join-Path $devRoot 'state\watch.lock'
$maintenanceRoot = Join-Path $devRoot 'maintenance'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$watcherTask = 'CSIT DEV Ready Watcher'
$installedWatcher = Join-Path $devRoot 'automation\Watch-DevReady.ps1'
$installedDeployer = Join-Path $devRoot 'automation\Deploy-DevReady.ps1'
$transitionFile = Join-Path $devRoot 'state\nas-business-hierarchy-transition.json'
$nasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'
$approvedWorkspaceRoot = '\\AI-NAS\Workspace_B'
$oldDocumentRoot = Join-Path $nasRoot 'DocumentLibrary'
$oldRawRoot = Join-Path $nasRoot 'RawData'
$newRawRoot = Join-Path $TechnicalRoot 'RawData'
$oldEquipmentRoot = Join-Path $nasRoot 'Assets\equipment'
$oldGoodsRoot = Join-Path $nasRoot 'Assets\goods'
$oldTestReportRoot = Join-Path $nasRoot 'TestReports'
$oldThreeLevelRoots = @{
    certification = Join-Path $nasRoot 'ThreeLevel\certification'
    verification = Join-Path $nasRoot 'ThreeLevel\verification'
}
$legacyTopLevels = @('DocumentLibrary','RawData','Assets','ThreeLevel','TestReports')
$lock = $null
$watchLock = $null
$runtimeBackup = $null
$databaseChanged = $false
$oldDevPid = 0
$productionPid = 0
$legacyBackupRoot = $null
$legacyMoves = [Collections.Generic.List[object]]::new()
$transitionActivated = $false

function Test-ReentryRequested {
    $values = @($ReentryEvidenceDirectory, $ApprovedPriorApplyCommitSha,
        $ApprovedPriorManifestSha256)
    $specified = @($values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($specified -notin @(0, 3)) {
        throw 'Re-entry requires EvidenceDirectory, prior Apply commit SHA, and prior manifest SHA256 together.'
    }
    return $specified -eq 3
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Only a local Administrator may run the DEV NAS hierarchy reconciliation.'
    }
}

function Get-SingleListenerPid([int] $Port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -ne 1) { throw "Port $Port must have exactly one listener." }
    return [int]$listeners[0]
}

function Assert-CurrentDevRuntime($Current) {
    $runtimePid = [int]$Current.Pid
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$runtimePid"
    if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
        $process.ExecutablePath -notlike "*$($Current.Sha)*") {
        throw 'The recorded DEV process is not the current SHA-scoped runtime.'
    }
    foreach ($port in @(7296, 7444)) {
        if ((Get-SingleListenerPid $port) -ne $runtimePid) {
            throw "DEV port $port is not served by the recorded current runtime."
        }
    }
}

function Enter-DevNasMaintenanceLocks([string] $WatcherLockPath, [string] $DeploymentLockPath) {
    $watcher = $null
    try {
        $watcher = [IO.File]::Open($WatcherLockPath,[IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    } catch [IO.IOException] { throw 'The DEV watcher is active; reconciliation was not started.' }
    try {
        $deployment = [IO.File]::Open($DeploymentLockPath,[IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    } catch [IO.IOException] {
        $watcher.Dispose()
        throw 'A DEV deployment or maintenance operation is already running.'
    }
    return [pscustomobject]@{ Watcher=$watcher; Deployment=$deployment }
}

function Get-Connection {
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    $raw = [string]$runtime.PSObject.Properties['ConnectionStrings:Postgres'].Value
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString($raw)
    if ([string]$connection['Host'] -notin @('localhost','127.0.0.1') -or
        [string]$connection['Database'] -ne 'sit_department_system_dev' -or
        [string]$connection['Username'] -ne 'csit_runtime_dev' -or
        -not $connection.ContainsKey('Password')) {
        throw 'The protected runtime connection is not the authorized DEV database and role.'
    }
    return $connection
}

function Invoke-DevSql($Connection, [string] $Sql) {
    $env:PGPASSWORD = [string]$Connection['Password']
    try {
        $result = @($Sql | & $psql -X -w -q -t -A -F '|' -v ON_ERROR_STOP=1 `
            -h ([string]$Connection['Host']) -p ([string]$Connection['Port']) `
            -U ([string]$Connection['Username']) -d ([string]$Connection['Database']) 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('DEV database operation failed: ' + ($result -join ' ')) }
        return @($result | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Invoke-DevCsv($Connection, [string] $Query) {
    $env:PGPASSWORD = [string]$Connection['Password']
    try {
        $result = @($Query | & $psql -X -w -q --csv -v ON_ERROR_STOP=1 `
            -h ([string]$Connection['Host']) -p ([string]$Connection['Port']) `
            -U ([string]$Connection['Username']) -d ([string]$Connection['Database']) 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('DEV database query failed: ' + ($result -join ' ')) }
        return @(($result -join "`n") | ConvertFrom-Csv)
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Quote-Sql([string] $Value) { return "'" + $Value.Replace("'", "''") + "'" }

function Get-RelativePath([string] $Root, [string] $Path) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped its approved root: root=$fullRoot path=$fullPath"
    }
    return $fullPath.Substring($fullRoot.Length)
}

function Assert-SeparateTechnicalRoot {
    $workspace = [IO.Path]::GetFullPath($approvedWorkspaceRoot).TrimEnd('\') + '\'
    $formal = [IO.Path]::GetFullPath($nasRoot).TrimEnd('\')
    $technical = [IO.Path]::GetFullPath($TechnicalRoot).TrimEnd('\')
    if (-not $technical.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase) -or
        $technical.Equals($workspace.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
        $technical.Equals($formal, [StringComparison]::OrdinalIgnoreCase) -or
        $technical.StartsWith($formal + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $formal.StartsWith($technical + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'TechnicalRoot must be under the approved NAS workspace and outside the formal business root.'
    }
    if (-not (Test-Path -LiteralPath $newRawRoot -PathType Container)) {
        throw 'The confirmed technical RawData root must exist before reconciliation.'
    }
}

function Assert-PathWithin([string] $Path, [string] $Root, [string] $Role) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role escaped its approved root: root=$fullRoot path=$fullPath"
    }
}

function Test-PathWithin([string] $Path, [string] $Root) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return [IO.Path]::GetFullPath($Path).StartsWith(
        $fullRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Get-SafeSegment([string] $Value, [string] $StableId = '') {
    $original = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($original)) { throw 'Formal business folder name is blank.' }
    $invalid = [Collections.Generic.HashSet[char]]::new([IO.Path]::GetInvalidFileNameChars())
    $null = $invalid.Add('/'); $null = $invalid.Add('\')
    $characters = foreach ($character in $original.ToCharArray()) {
        if ($invalid.Contains($character) -or [char]::IsControl($character)) { '_' } else { $character }
    }
    $cleaned = ((-join $characters) -split ' ' | Where-Object { $_ }) -join ' '
    $cleaned = $cleaned.Trim('.', ' ')
    if ($cleaned -in @('CON','PRN','AUX','NUL','COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9',
            'LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9')) { $cleaned = '_' + $cleaned }
    if ([string]::IsNullOrWhiteSpace($cleaned)) { $cleaned = '_' }
    $suffix = if ($cleaned -cne $original -and $StableId) { '~' + $StableId.Replace('-','').Substring(0,8) } else { '' }
    if (($cleaned.Length + $suffix.Length) -gt 120) {
        if ($StableId) { $suffix = '~' + $StableId.Replace('-','').Substring(0,8) }
        $cleaned = $cleaned.Substring(0,120 - $suffix.Length).TrimEnd('.', ' ')
    }
    return $cleaned + $suffix
}

function Add-ManifestItem($Rows, [string] $Kind, [string] $Key, [string] $Source,
    [string] $Target, [long] $ExpectedSize, [string] $ExpectedSha, [string] $DbValue,
    [string] $NewDbValue, [string] $ApprovedSourceRoot, [string] $ApprovedTargetRoot) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $targetPath = [IO.Path]::GetFullPath($Target)
    Assert-PathWithin $sourcePath $ApprovedSourceRoot 'Source path'
    Assert-PathWithin $targetPath $ApprovedTargetRoot 'Target path'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Database-referenced source file is absent: kind=$Kind key=$Key path=$sourcePath"
    }
    $sourceFile = Get-Item -LiteralPath $sourcePath
    $sourceSha = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedSize -ge 0 -and $sourceFile.Length -ne $ExpectedSize) {
        throw "Database size does not match source: kind=$Kind key=$Key"
    }
    if ($ExpectedSha -and $ExpectedSha.ToLowerInvariant() -ne $sourceSha) {
        throw "Database SHA256 does not match source: kind=$Kind key=$Key"
    }
    $Rows.Add([pscustomobject]@{
        Kind=$Kind; Key=$Key; SourcePath=$sourcePath; TargetPath=$targetPath
        Size=[long]$sourceFile.Length; Sha256=$sourceSha; DbValue=$DbValue; NewDbValue=$NewDbValue
    })
}

function Assert-CurrentRoots($Connection) {
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    if ([string]$runtime.PSObject.Properties['DocumentStorage:RootPath'].Value -ne $oldDocumentRoot -or
        [string]$runtime.PSObject.Properties['RawData:NasRootPath'].Value -ne $oldRawRoot) {
        throw 'DEV runtime roots do not match the reviewed pre-reconciliation NAS state.'
    }
    $expected = @(
        "AssetRootPath|equipment|$oldEquipmentRoot", "AssetRootPath|goods|$oldGoodsRoot",
        "TestReportRootPath|default|$oldTestReportRoot",
        "ThreeLevelRootPath|certification|$($oldThreeLevelRoots.certification)",
        "ThreeLevelRootPath|verification|$($oldThreeLevelRoots.verification)")
    $actual = @(Invoke-DevSql $Connection @'
BEGIN TRANSACTION READ ONLY;
SELECT category, name, value FROM system_options
WHERE is_enabled AND category IN ('AssetRootPath','ThreeLevelRootPath','TestReportRootPath')
ORDER BY category,name;
COMMIT;
'@)
    if (($actual -join "`n") -cne (($expected | Sort-Object) -join "`n")) {
        throw 'DEV SystemOptions roots do not match the reviewed pre-reconciliation NAS state.'
    }
}

function Get-Manifest($Connection) {
    $rows = [Collections.Generic.List[object]]::new()
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    $stagingRoot = [string]$runtime.PSObject.Properties['DocumentStaging:RootPath'].Value

    $documents = Invoke-DevCsv $Connection @'
SELECT f.id, f.storage_area, f.relative_path, f.standard_file_name, f.file_size, f.sha256,
       l1.id AS l1_id, l1.name AS l1_name, l2.id AS l2_id, l2.name AS l2_name,
       l3.id AS l3_id, l3.name AS l3_name
FROM document_files f
JOIN document_folders l3 ON l3.id=f.folder_id
JOIN document_folders l2 ON l2.id=l3.parent_id
JOIN document_folders l1 ON l1.id=l2.parent_id
ORDER BY f.id
'@
    foreach ($item in $documents) {
        $relative = Join-Path (Get-SafeSegment $item.l1_name $item.l1_id) `
            (Join-Path (Get-SafeSegment $item.l2_name $item.l2_id) `
                (Join-Path (Get-SafeSegment $item.l3_name $item.l3_id) $item.standard_file_name))
        $sourceRoot = if ($item.storage_area -eq 'Library') { $oldDocumentRoot } else { $stagingRoot }
        $targetRoot = if ($item.storage_area -eq 'Library') { $nasRoot } else { $stagingRoot }
        Add-ManifestItem $rows 'Document' $item.id (Join-Path $sourceRoot $item.relative_path) `
            (Join-Path $targetRoot $relative) ([long]$item.file_size) $item.sha256 `
            $item.relative_path $relative $sourceRoot $targetRoot
    }

    $vendors = Invoke-DevCsv $Connection @'
SELECT f.id, f.folder_path, f.stored_file_name, f.file_size, f.sha256, v.storage_folder_name
FROM vendor_files f JOIN vendors v ON v.id=f.vendor_id ORDER BY f.id
'@
    foreach ($item in $vendors) {
        $folder = Join-Path $nasRoot (Join-Path '設備儀器管理\廠商資訊' $item.storage_folder_name)
        Add-ManifestItem $rows 'Vendor' $item.id (Join-Path $item.folder_path $item.stored_file_name) `
            (Join-Path $folder $item.stored_file_name) ([long]$item.file_size) $item.sha256 `
            $item.folder_path $folder $oldEquipmentRoot $nasRoot
    }

    $equipment = Invoke-DevCsv $Connection @'
SELECT f."Id" AS id, f."FilePath" AS folder_path, f."FileName" AS file_name,
       f."FileSize" AS file_size, coalesce(f.sha256,'') AS sha256,
       a."Id" AS apparatus_id, a."ProductsId" AS products_id, a."Name" AS name
FROM apparatus_files f JOIN apparatus a ON a."Id"=f."ApparatusId"
WHERE lower(a."ModuleCode")='equipment' ORDER BY f."Id"
'@
    foreach ($item in $equipment) {
        if ([string]::IsNullOrWhiteSpace($item.products_id)) {
            throw "Equipment has no property number; classification is ambiguous: $($item.apparatus_id)"
        }
        $storageFolder = Get-SafeSegment ($item.products_id.Trim() + ' - ' + $item.name.Trim())
        $folder = Join-Path $nasRoot (Join-Path '設備儀器管理\設備資訊' $storageFolder)
        Add-ManifestItem $rows 'Equipment' $item.id (Join-Path $item.folder_path $item.file_name) `
            (Join-Path $folder $item.file_name) ([long]$item.file_size) $item.sha256 `
            $item.folder_path $folder $oldEquipmentRoot $nasRoot
        $rows[$rows.Count - 1] | Add-Member -NotePropertyName ParentKey -NotePropertyValue $item.apparatus_id
        $rows[$rows.Count - 1] | Add-Member -NotePropertyName StorageFolder -NotePropertyValue $storageFolder
    }

    $caseFiles = Invoke-DevCsv $Connection @'
SELECT f.id, f.file_path, f.file_name, f.file_size, coalesce(f.sha256,'') AS sha256,
       f.file_kind, f.document_id, f.version_no, r.id AS record_id, lower(m.code) AS module_code,
       coalesce(t.model_name, va.product_model, '') AS model_name
FROM module_case_files f
JOIN module_records r ON r.id=f.record_id JOIN modules m ON m.id=r.module_id
LEFT JOIN module_record_tasks t ON t.id=f.task_id
LEFT JOIN LATERAL (
  SELECT product_model FROM verification_applications
  WHERE module_record_id=f.record_id AND coalesce(product_model,'')<>''
  ORDER BY submitted_at DESC NULLS LAST LIMIT 1
) va ON true
ORDER BY f.id
'@
    foreach ($item in $caseFiles) {
        $source = Join-Path $item.file_path $item.file_name
        if ($item.file_kind -eq 'TestReport') {
            if ([string]::IsNullOrWhiteSpace($item.model_name)) {
                throw "Test report Model Name is ambiguous; migration stopped for file $($item.id)."
            }
            $folder = Join-Path $nasRoot (Join-Path '產品驗證資料' `
                (Join-Path (Get-SafeSegment $item.model_name) '測試報告'))
            $approvedSourceRoot = $oldTestReportRoot
        } else {
            if (-not $oldThreeLevelRoots.ContainsKey($item.module_code)) {
                throw "Unknown three-level module root for file $($item.id): $($item.module_code)"
            }
            $oldRoot = [string]$oldThreeLevelRoots[$item.module_code]
            $relativeFolder = Get-RelativePath $oldRoot $item.file_path
            $levels = @($relativeFolder -split '[\\/]' | Where-Object { $_ })
            if ($levels.Count -eq 2) {
                $relativeFolder = Join-Path $relativeFolder '一般文件'
            } elseif ($levels.Count -ne 3) {
                throw "Case file does not have an approved L1/L2/L3 classification: $($item.id)"
            }
            $folder = Join-Path $nasRoot $relativeFolder
            $approvedSourceRoot = $oldRoot
        }
        Add-ManifestItem $rows 'CaseFile' $item.id $source (Join-Path $folder $item.file_name) `
            ([long]$item.file_size) $item.sha256 $item.file_path $folder $approvedSourceRoot $nasRoot
    }

    if (Test-Path -LiteralPath $oldRawRoot -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $oldRawRoot -File -Recurse -Force) {
            $relative = Get-RelativePath $oldRawRoot $file.FullName
            Add-ManifestItem $rows 'TechnicalRawData' $relative $file.FullName `
                (Join-Path $newRawRoot $relative) $file.Length '' '' '' $oldRawRoot $newRawRoot
        }
    }

    $collisions = $rows | Group-Object { $_.TargetPath.ToLowerInvariant() } | Where-Object Count -gt 1
    foreach ($collision in $collisions) {
        throw "Duplicate sources map to one target and require classification review: $($collision.Name)"
    }
    return @($rows)
}

function Copy-And-Verify($Manifest) {
    foreach ($item in $Manifest) {
        $parent = Split-Path -Parent $item.TargetPath
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        if (Test-Path -LiteralPath $item.TargetPath -PathType Leaf) {
            $target = Get-Item -LiteralPath $item.TargetPath
            $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($target.Length -ne $item.Size -or $hash -ne $item.Sha256) {
                throw "Existing target differs and will not be overwritten: $($item.TargetPath)"
            }
        } else {
            Copy-Item -LiteralPath $item.SourcePath -Destination $item.TargetPath
        }
        $verified = Get-Item -LiteralPath $item.TargetPath
        $verifiedHash = (Get-FileHash -LiteralPath $verified.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($verified.Length -ne $item.Size -or $verifiedHash -ne $item.Sha256) {
            throw "Target SHA256 verification failed: $($item.TargetPath)"
        }
    }
}

function Assert-ExistingTargets($Manifest) {
    foreach ($item in $Manifest) {
        if (-not (Test-Path -LiteralPath $item.TargetPath -PathType Leaf)) {
            throw "Re-entry target is missing: kind=$($item.Kind) key=$($item.Key)"
        }
        $target = Get-Item -LiteralPath $item.TargetPath
        $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($target.Length -ne [long]$item.Size -or $hash -ne ([string]$item.Sha256).ToLowerInvariant()) {
            throw "Re-entry target SHA256 verification failed: kind=$($item.Kind) key=$($item.Key)"
        }
    }
}

function Assert-ExistingSources($Manifest) {
    foreach ($item in $Manifest) {
        if (-not (Test-Path -LiteralPath $item.SourcePath -PathType Leaf)) {
            throw "Re-entry source is missing: kind=$($item.Kind) key=$($item.Key)"
        }
        $source = Get-Item -LiteralPath $item.SourcePath
        $hash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($source.Length -ne [long]$item.Size -or $hash -ne ([string]$item.Sha256).ToLowerInvariant()) {
            throw "Re-entry source SHA256 verification failed: kind=$($item.Kind) key=$($item.Key)"
        }
    }
}

function Assert-ReentryInventory($PriorManifest) {
    $allowedFormal = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $allowedTechnical = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $PriorManifest) {
        foreach ($path in @([string]$item.SourcePath, [string]$item.TargetPath)) {
            if (Test-PathWithin $path $nasRoot) {
                $null = $allowedFormal.Add([IO.Path]::GetFullPath($path))
            }
        }
        if (Test-PathWithin ([string]$item.TargetPath) $newRawRoot) {
            $null = $allowedTechnical.Add([IO.Path]::GetFullPath([string]$item.TargetPath))
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $nasRoot -File -Recurse -Force) {
        if (-not $allowedFormal.Contains([IO.Path]::GetFullPath($file.FullName))) {
            throw 'Re-entry formal storage contains an unclassified file.'
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $newRawRoot -File -Recurse -Force) {
        if (-not $allowedTechnical.Contains([IO.Path]::GetFullPath($file.FullName))) {
            throw 'Re-entry technical target contains an unclassified file.'
        }
    }
}

function Assert-ReentryState($Fence, [string] $EvidenceDirectory,
    [string] $PriorApplyCommitSha, [string] $PriorManifestSha256,
    $Current, $CurrentManifest, [string] $RuntimePath) {
    $evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
    $approvedMaintenance = [IO.Path]::GetFullPath($maintenanceRoot).TrimEnd('\') + '\'
    if (-not ($evidence + '\').StartsWith($approvedMaintenance,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Re-entry evidence is outside the protected maintenance root.'
    }
    if ([int]$Fence.SchemaVersion -ne 1 -or [string]$Fence.Status -cne 'RolledBack') {
        throw 'Re-entry requires the designated SchemaVersion 1 RolledBack fence.'
    }
    if ([string]$Fence.ApplyCommitSha -cne $PriorApplyCommitSha -or
        -not [IO.Path]::GetFullPath([string]$Fence.EvidenceDirectory).Equals(
            $evidence, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Re-entry fence does not match the approved prior Apply evidence.'
    }
    if (Test-Path -LiteralPath (Join-Path $evidence 'evidence-integrity.json')) {
        throw 'Re-entry refuses prior evidence that is sealed as a completed Apply.'
    }
    foreach ($name in @('summary.json','file-manifest.csv','database-switch.sql',
            'database-rollback.sql','runtime.before.json','current.before.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $evidence $name) -PathType Leaf)) {
            throw "Re-entry prior evidence is incomplete: $name"
        }
    }
    $manifestPath = Join-Path $evidence 'file-manifest.csv'
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ($manifestHash -cne $PriorManifestSha256.ToUpperInvariant()) {
        throw 'Re-entry prior manifest SHA256 does not match the explicit approval.'
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'summary.json') -Raw |
        ConvertFrom-Json
    if ([string]$summary.Mode -cne 'Apply' -or
        [string]$summary.ApplyCommitSha -cne $PriorApplyCommitSha -or
        [string]$summary.PreMigrationRuntimeSha -cne [string]$Fence.PreMigrationRuntimeSha -or
        -not [IO.Path]::GetFullPath([string]$summary.EvidenceDirectory).Equals(
            $evidence, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Re-entry prior summary is unrelated to the designated fence.'
    }
    if (Test-Path -LiteralPath ([string]$summary.LegacySourceBackup)) {
        throw 'Re-entry refuses a prior transaction whose LegacySourceBackup exists.'
    }
    if ([string]$Current.Sha -cne [string]$Fence.PreMigrationRuntimeSha) {
        throw 'Re-entry current DEV runtime SHA is not the pre-migration runtime.'
    }
    if ((Get-FileHash -LiteralPath $RuntimePath -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $evidence 'runtime.before.json') -Algorithm SHA256).Hash) {
        throw 'Re-entry runtime configuration is not the pre-migration runtime configuration.'
    }

    $prior = @(Import-Csv -LiteralPath $manifestPath)
    if ($prior.Count -ne 155 -or
        @($prior | Where-Object Kind -eq 'TechnicalRawData').Count -ne 148 -or
        @($prior | Where-Object Kind -ne 'TechnicalRawData').Count -ne 7) {
        throw 'Re-entry prior manifest does not contain the approved 7 formal and 148 technical files.'
    }
    $priorByIdentity = @{}
    foreach ($item in $prior) {
        $identity = "$($item.Kind)|$($item.Key)"
        if ($priorByIdentity.ContainsKey($identity)) {
            throw 'Re-entry prior manifest contains a duplicate record identity.'
        }
        $priorByIdentity[$identity] = $item
    }
    if (@($CurrentManifest).Count -ne $prior.Count) {
        throw 'Re-entry current DB/source manifest count differs from prior evidence.'
    }
    foreach ($item in @($CurrentManifest)) {
        $identity = "$($item.Kind)|$($item.Key)"
        if (-not $priorByIdentity.ContainsKey($identity)) {
            throw 'Re-entry current DB/source manifest contains an unapproved record.'
        }
        $expected = $priorByIdentity[$identity]
        if ([string]$item.DbValue -cne [string]$expected.DbValue -or
            [string]$item.NewDbValue -cne [string]$expected.NewDbValue -or
            [IO.Path]::GetFullPath([string]$item.SourcePath) -ine
                [IO.Path]::GetFullPath([string]$expected.SourcePath) -or
            [IO.Path]::GetFullPath([string]$item.TargetPath) -ine
                [IO.Path]::GetFullPath([string]$expected.TargetPath) -or
            [long]$item.Size -ne [long]$expected.Size -or
            [string]$item.Sha256 -ine [string]$expected.Sha256) {
            throw "Re-entry current DB/source manifest differs: kind=$($item.Kind) key=$($item.Key)"
        }
    }
    Assert-ExistingSources $prior
    Assert-ExistingTargets $prior
    Assert-ReentryInventory $prior
    return [pscustomobject]@{ Summary=$summary; Manifest=$prior; ManifestSha256=$manifestHash }
}

function New-ReentryPredecessorArchive([string] $ActiveFencePath,
    [string] $NewEvidenceDirectory, [string] $PriorEvidenceDirectory,
    [string] $PriorApplyCommitSha, [string] $PriorManifestSha256,
    [string] $NewApplyCommitSha) {
    $archivePath = Join-Path $NewEvidenceDirectory 'predecessor-fence.json'
    $relationPath = Join-Path $NewEvidenceDirectory 'reentry-predecessor.json'
    if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $relationPath)) {
        throw 'Re-entry predecessor archive already exists in the new evidence.'
    }
    $fenceHash = (Get-FileHash -LiteralPath $ActiveFencePath -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $ActiveFencePath -Destination $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -cne $fenceHash) {
        throw 'Re-entry predecessor fence archive verification failed.'
    }
    $relation = [ordered]@{
        SchemaVersion = 1
        PriorFenceSha256 = $fenceHash
        PriorEvidenceDirectory = [IO.Path]::GetFullPath($PriorEvidenceDirectory)
        PriorManifestSha256 = $PriorManifestSha256.ToUpperInvariant()
        PriorApplyCommitSha = $PriorApplyCommitSha
        NewApplyCommitSha = $NewApplyCommitSha
    }
    $relation | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $relationPath -Encoding UTF8
    return $relation
}

function Set-TransitionFenceAtomically([string] $ActiveFencePath,
    [string] $ExpectedActiveSha256, $Transition) {
    if (-not (Test-Path -LiteralPath $ActiveFencePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $ActiveFencePath -Algorithm SHA256).Hash -cne
            $ExpectedActiveSha256) {
        throw 'The active transition fence changed during re-entry initialization.'
    }
    $temporary = $ActiveFencePath + '.tmp'
    $Transition | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $ActiveFencePath -Force
}

function Assert-FormalInventory($Manifest, [bool] $AllowLegacy) {
    $allowedTopLevels = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    if ($AllowLegacy) {
        foreach ($name in $legacyTopLevels) { $null = $allowedTopLevels.Add($name) }
    }
    foreach ($item in $Manifest | Where-Object { $_.Kind -ne 'TechnicalRawData' }) {
        if (-not (Test-PathWithin $item.TargetPath $nasRoot)) { continue }
        $relative = Get-RelativePath $nasRoot $item.TargetPath
        $parts = @($relative -split '[\\/]' | Where-Object { $_ })
        if ($parts.Count -ne 4) {
            throw "Formal target is not exactly L1/L2/L3/file: $($item.TargetPath)"
        }
        if ($parts[-1].ToLowerInvariant() -in @('rawdata.json','km-manifest.json')) {
            throw "Technical metadata file is not allowed in formal business storage: $($item.TargetPath)"
        }
        $null = $allowedTopLevels.Add($parts[0])
    }
    foreach ($entry in Get-ChildItem -LiteralPath $nasRoot -Force) {
        if (-not $entry.PSIsContainer) {
            throw "Formal business root contains an unclassified file: $($entry.FullName)"
        }
        if (-not $allowedTopLevels.Contains($entry.Name)) {
            throw "Formal business root contains an unclassified directory: $($entry.FullName)"
        }
    }
}

function Assert-EveryLegacyFileClassified($Manifest) {
    $classified = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Manifest) {
        if (Test-PathWithin $item.SourcePath $nasRoot) {
            $null = $classified.Add([IO.Path]::GetFullPath($item.SourcePath))
        }
    }
    foreach ($name in $legacyTopLevels) {
        $root = Join-Path $nasRoot $name
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
            if (-not $classified.Contains([IO.Path]::GetFullPath($file.FullName))) {
                throw "Legacy file has no approved classification: $($file.FullName)"
            }
        }
    }
}

function Move-LegacySourcesToTechnicalBackup {
    if ([string]::IsNullOrWhiteSpace($legacyBackupRoot) -or
        [IO.Path]::GetFullPath($legacyBackupRoot).StartsWith(
            [IO.Path]::GetFullPath($nasRoot).TrimEnd('\') + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Legacy source backup is not safely separated from formal business storage.'
    }
    New-Item -ItemType Directory -Path $legacyBackupRoot | Out-Null
    foreach ($name in $legacyTopLevels) {
        $source = Join-Path $nasRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
        $target = Join-Path $legacyBackupRoot $name
        if (Test-Path -LiteralPath $target) { throw "Legacy backup target already exists: $target" }
        Move-Item -LiteralPath $source -Destination $target
        $legacyMoves.Add([pscustomobject]@{ Source=$source; Target=$target })
    }
}

function Restore-LegacySources {
    for ($index = $legacyMoves.Count - 1; $index -ge 0; $index--) {
        $move = $legacyMoves[$index]
        if (-not (Test-Path -LiteralPath $move.Target -PathType Container)) { continue }
        if (Test-Path -LiteralPath $move.Source) {
            throw "Cannot restore legacy source because its original path exists: $($move.Source)"
        }
        Move-Item -LiteralPath $move.Target -Destination $move.Source
    }
}

function Verify-LegacyBackup($Manifest) {
    foreach ($item in $Manifest) {
        if (-not (Test-PathWithin $item.SourcePath $nasRoot)) { continue }
        $relative = Get-RelativePath $nasRoot $item.SourcePath
        $backup = Join-Path $legacyBackupRoot $relative
        if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
            throw "Retained legacy source is missing from technical backup: $backup"
        }
        $file = Get-Item -LiteralPath $backup
        $hash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($file.Length -ne $item.Size -or $hash -ne $item.Sha256) {
            throw "Retained legacy source SHA256 verification failed: $backup"
        }
    }
}

function New-DatabaseSwitchSql($Manifest, [switch] $Rollback) {
    $statements = [Collections.Generic.List[string]]::new()
    $statements.Add('BEGIN;')
    foreach ($item in $Manifest | Where-Object {
            $_.Kind -in @('Document','Vendor','Equipment','CaseFile') }) {
        $old = if ($Rollback) { $item.NewDbValue } else { $item.DbValue }
        $new = if ($Rollback) { $item.DbValue } else { $item.NewDbValue }
        $mapping = switch ($item.Kind) {
            Document { [pscustomobject]@{
                Table='document_files'; Field='relative_path'; IdField='id'
            } }
            Vendor { [pscustomobject]@{
                Table='vendor_files'; Field='folder_path'; IdField='id'
            } }
            Equipment { [pscustomobject]@{
                Table='apparatus_files'; Field='FilePath'; IdField='Id'
            } }
            CaseFile { [pscustomobject]@{
                Table='module_case_files'; Field='file_path'; IdField='id'
            } }
        }
        if (-not $mapping) { throw "Unknown manifest kind for DB path switch: $($item.Kind)" }
        $quotedIdField = if ($mapping.IdField -ceq 'Id') { '"Id"' } else { $mapping.IdField }
        $quotedField = if ($mapping.Field -ceq 'FilePath') { '"FilePath"' } else { $mapping.Field }
        $predicate = "$quotedIdField=$(Quote-Sql $item.Key)::uuid"
        $statements.Add("UPDATE $($mapping.Table) SET $quotedField=$(Quote-Sql $new) WHERE $predicate AND $quotedField=$(Quote-Sql $old);")
        $verification = "SELECT 1 FROM $($mapping.Table) WHERE $predicate AND $quotedField=$(Quote-Sql $new)"
        $failure = "DB path switch verification failed: Kind=$($item.Kind) Table=$($mapping.Table) RecordId=$($item.Key) Field=$($mapping.Field)"
        $statements.Add("DO `$csit`$ BEGIN IF NOT EXISTS ($verification) THEN RAISE EXCEPTION $(Quote-Sql $failure); END IF; END `$csit`$;")
    }
    foreach ($equipment in $Manifest | Where-Object { $_.Kind -eq 'Equipment' } | Group-Object ParentKey) {
        $folder = $equipment.Group[0].StorageFolder
        if ($Rollback) {
            $original = Split-Path -Leaf (Split-Path -Parent $equipment.Group[0].SourcePath)
            $statements.Add("UPDATE apparatus SET storage_folder_name=$(Quote-Sql $original) WHERE `"Id`"=$(Quote-Sql $equipment.Name);")
        } else {
            $statements.Add("UPDATE apparatus SET storage_folder_name=$(Quote-Sql $folder) WHERE `"Id`"=$(Quote-Sql $equipment.Name);")
        }
        $expectedFolder = if ($Rollback) { $original } else { $folder }
        $statements.Add("DO `$csit`$ BEGIN IF NOT EXISTS (SELECT 1 FROM apparatus WHERE `"Id`"=$(Quote-Sql $equipment.Name) AND storage_folder_name=$(Quote-Sql $expectedFolder)) THEN RAISE EXCEPTION 'Equipment folder switch verification failed'; END IF; END `$csit`$;")
    }
    $fromRaw = if ($Rollback) { $newRawRoot } else { $oldRawRoot }
    $toRaw = if ($Rollback) { $oldRawRoot } else { $newRawRoot }
    foreach ($table in @('apparatus_files','module_case_files')) {
        foreach ($column in @('nas_folder_path','nas_file_path','raw_json_path')) {
            $quotedColumn = $column
            $statements.Add("UPDATE $table SET $quotedColumn=$(Quote-Sql $toRaw) || substring($quotedColumn from length($(Quote-Sql $fromRaw)) + 1) WHERE left(coalesce($quotedColumn,''),length($(Quote-Sql $fromRaw)))=$(Quote-Sql $fromRaw);")
            $statements.Add("DO `$csit`$ BEGIN IF EXISTS (SELECT 1 FROM $table WHERE left(coalesce($quotedColumn,''),length($(Quote-Sql $fromRaw)))=$(Quote-Sql $fromRaw)) THEN RAISE EXCEPTION 'RawData path switch verification failed'; END IF; END `$csit`$;")
        }
    }
    $roots = if ($Rollback) {
        @{
            'AssetRootPath|equipment'=$oldEquipmentRoot; 'AssetRootPath|goods'=$oldGoodsRoot
            'ThreeLevelRootPath|certification'=$oldThreeLevelRoots.certification
            'ThreeLevelRootPath|verification'=$oldThreeLevelRoots.verification
            'TestReportRootPath|default'=$oldTestReportRoot
        }
    } else {
        @{
            'AssetRootPath|equipment'=$nasRoot; 'AssetRootPath|goods'=$nasRoot
            'ThreeLevelRootPath|certification'=$nasRoot; 'ThreeLevelRootPath|verification'=$nasRoot
            'TestReportRootPath|default'=$nasRoot
        }
    }
    foreach ($key in $roots.Keys) {
        $parts = $key -split '\|',2
        $statements.Add("UPDATE system_options SET value=$(Quote-Sql $roots[$key]), updated_at=now() WHERE category=$(Quote-Sql $parts[0]) AND lower(name)=lower($(Quote-Sql $parts[1])) AND is_enabled;")
        $statements.Add("DO `$csit`$ BEGIN IF (SELECT count(*) FROM system_options WHERE category=$(Quote-Sql $parts[0]) AND lower(name)=lower($(Quote-Sql $parts[1])) AND is_enabled AND value=$(Quote-Sql $roots[$key])) <> 1 THEN RAISE EXCEPTION 'SystemOptions root switch verification failed'; END IF; END `$csit`$;")
    }
    $statements.Add('COMMIT;')
    return $statements -join "`n"
}

function Set-RuntimeRoots([switch] $Rollback) {
    if ($Rollback) {
        Copy-Item -LiteralPath $runtimeBackup -Destination $runtimeFile -Force
        return
    }
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    $runtime.PSObject.Properties['DocumentStorage:RootPath'].Value = $nasRoot
    $runtime.PSObject.Properties['RawData:NasRootPath'].Value = $newRawRoot
    $runtime | Add-Member -NotePropertyName 'FormalStorage:BusinessHierarchyEnabled' `
        -NotePropertyValue $true -Force
    $temporary = $runtimeFile + '.business-hierarchy.tmp'
    $runtime | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Set-Acl -LiteralPath $temporary -AclObject (Get-Acl -LiteralPath $runtimeFile)
    Move-Item -LiteralPath $temporary -Destination $runtimeFile -Force
}

if ($LoadFunctionsOnly) { return }

$isReentry = Test-ReentryRequested
Assert-Administrator
Assert-SeparateTechnicalRoot
foreach ($path in @($runtimeFile,$currentFile,$psql)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required DEV file is absent: $path" }
}
$productionPid = Get-SingleListenerPid 5010
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
$oldDevPid = [int]$current.Pid
$connection = Get-Connection
Assert-CurrentRoots $connection
$unknownAssetFileCount = [int](@(Invoke-DevSql $connection @'
SELECT count(*) FROM apparatus_files f
JOIN apparatus a ON a."Id"=f."ApparatusId"
WHERE lower(a."ModuleCode") <> 'equipment';
'@)[0])
if ($unknownAssetFileCount -ne 0) {
    throw "Unknown asset classifications have files and cannot be moved automatically: count=$unknownAssetFileCount"
}
$manifest = @(Get-Manifest $connection)
Assert-EveryLegacyFileClassified $manifest
Assert-FormalInventory $manifest $true
$summary = [ordered]@{
    Mode = if ($Apply) { 'Apply' } else { 'Audit' }
    PreMigrationRuntimeSha = [string]$current.Sha
    ApplyCommitSha = if ($Apply) { $ApprovedApplyCommitSha } else { $null }
    NasRoot = $nasRoot
    TechnicalRoot = [IO.Path]::GetFullPath($TechnicalRoot)
    FormalFileCount = @($manifest | Where-Object Kind -ne 'TechnicalRawData').Count
    TechnicalFileCount = @($manifest | Where-Object Kind -eq 'TechnicalRawData').Count
    TotalFileCount = $manifest.Count
    TotalBytes = [long](($manifest | Measure-Object Size -Sum).Sum)
    OriginalsRetained = $true
    CompletedKmJobsTouched = $false
    FailedKmJobsTouched = $false
    ProductionPid = $productionPid
}
if (-not $Apply) {
    $summary | ConvertTo-Json -Depth 5
    Write-Output 'DEV_NAS_BUSINESS_HIERARCHY_AUDIT_ONLY'
    exit 0
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$head = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ApprovedApplyCommitSha) {
    throw 'ApprovedApplyCommitSha does not match the reviewed source checkout.'
}
if (-not $isReentry -and (Test-Path -LiteralPath $transitionFile)) {
    throw 'A DEV NAS hierarchy transition fence already exists.'
}
if ($isReentry -and -not (Test-Path -LiteralPath $transitionFile -PathType Leaf)) {
    throw 'The explicitly approved RolledBack transition fence is absent.'
}
if ($isReentry) {
    $initialFence = Get-Content -LiteralPath $transitionFile -Raw | ConvertFrom-Json
    if ([string]$initialFence.Status -cne 'RolledBack' -or
        [string]$initialFence.ApplyCommitSha -cne $ApprovedPriorApplyCommitSha -or
        -not [IO.Path]::GetFullPath([string]$initialFence.EvidenceDirectory).Equals(
            [IO.Path]::GetFullPath($ReentryEvidenceDirectory),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The active fence is not the explicitly approved RolledBack predecessor.'
    }
}
foreach ($probeRoot in @($nasRoot, $newRawRoot)) {
    $probeOutput = @(& (Join-Path $PSScriptRoot 'Test-DevNasAccess.ps1') -NasRoot $probeRoot)
    if ($LASTEXITCODE -ne 0 -or $probeOutput -notcontains 'DEV_NAS_ACCESS_PASS') {
        throw "LOCAL SERVICE NAS read/write access is not verified for $probeRoot; reconciliation was not started."
    }
}
$maintenanceLocks = Enter-DevNasMaintenanceLocks $watchLockPath $lockPath
$watchLock = $maintenanceLocks.Watcher
$lock = $maintenanceLocks.Deployment
foreach ($automation in @(
        @{ Installed=$installedWatcher; Source=(Join-Path $PSScriptRoot 'Watch-DevReady.ps1') },
        @{ Installed=$installedDeployer; Source=(Join-Path $PSScriptRoot 'Deploy-DevReady.ps1') })) {
    if (-not (Test-Path -LiteralPath $automation.Installed -PathType Leaf) -or
        (Get-FileHash -LiteralPath $automation.Installed -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $automation.Source -Algorithm SHA256).Hash) {
        $lock.Dispose(); $lock = $null
        $watchLock.Dispose(); $watchLock = $null
        throw 'The protected DEV watcher and deployer must be updated to this reviewed transition-aware version before Apply.'
    }
}
if (-not $isReentry -and (Test-Path -LiteralPath $transitionFile)) {
    $lock.Dispose(); $lock = $null
    $watchLock.Dispose(); $watchLock = $null
    throw 'A DEV NAS hierarchy transition fence appeared while acquiring maintenance locks.'
}
$reentryState = $null
$priorFenceSha256 = $null
if ($isReentry) {
    try {
        if (-not (Test-Path -LiteralPath $transitionFile -PathType Leaf)) {
            throw 'The approved RolledBack transition fence disappeared while acquiring maintenance locks.'
        }
        $priorFenceSha256 = (Get-FileHash -LiteralPath $transitionFile -Algorithm SHA256).Hash
        $priorFence = Get-Content -LiteralPath $transitionFile -Raw | ConvertFrom-Json
        $current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
        $oldDevPid = [int]$current.Pid
        if ((Get-SingleListenerPid 5010) -ne $productionPid) {
            throw 'Production 5010 PID changed before re-entry initialization.'
        }
        Assert-CurrentDevRuntime $current
        Assert-CurrentRoots $connection
        $manifest = @(Get-Manifest $connection)
        Assert-EveryLegacyFileClassified $manifest
        Assert-FormalInventory $manifest $true
        $reentryState = Assert-ReentryState $priorFence $ReentryEvidenceDirectory `
            $ApprovedPriorApplyCommitSha $ApprovedPriorManifestSha256 $current $manifest $runtimeFile
    } catch {
        $lock.Dispose(); $lock = $null
        $watchLock.Dispose(); $watchLock = $null
        throw
    }
}
try {
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$evidenceDir = Join-Path $maintenanceRoot "nas-business-hierarchy-$stamp-$($ApprovedApplyCommitSha.Substring(0,8))"
$legacyBackupRoot = Join-Path $TechnicalRoot "LegacySourceBackup\nas-business-hierarchy-$stamp-$($ApprovedApplyCommitSha.Substring(0,8))"
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$runtimeBackup = Join-Path $evidenceDir 'runtime.before.json'
Copy-Item -LiteralPath $runtimeFile -Destination $runtimeBackup
Copy-Item -LiteralPath $currentFile -Destination (Join-Path $evidenceDir 'current.before.json')
$manifest | Export-Csv -LiteralPath (Join-Path $evidenceDir 'file-manifest.csv') -NoTypeInformation -Encoding UTF8
$summary.EvidenceDirectory = $evidenceDir
$summary.LegacySourceBackup = $legacyBackupRoot
$reentryRelation = $null
if ($isReentry) {
    $reentryRelation = New-ReentryPredecessorArchive $transitionFile $evidenceDir `
        $ReentryEvidenceDirectory $ApprovedPriorApplyCommitSha `
        $ApprovedPriorManifestSha256 $ApprovedApplyCommitSha
    $summary.ReentryPredecessor = $reentryRelation
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDir 'summary.json') -Encoding UTF8
$transition = [ordered]@{
    SchemaVersion = 1
    Status = 'AwaitingCompatibleDeployment'
    PreMigrationRuntimeSha = [string]$current.Sha
    ApplyCommitSha = $ApprovedApplyCommitSha
    EvidenceDirectory = $evidenceDir
}
if ($isReentry) {
    $transition.ReentryPredecessor = $reentryRelation
    Set-TransitionFenceAtomically $transitionFile $priorFenceSha256 $transition
} else {
    $transition | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($transitionFile + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($transitionFile + '.tmp') -Destination $transitionFile -Force
}
$transitionActivated = $true

    Assert-CurrentDevRuntime $current
    Stop-Process -Id $oldDevPid -Force
    $stopped = Get-Process -Id $oldDevPid -ErrorAction SilentlyContinue
    if ($stopped) { $null = $stopped.WaitForExit(15000) }
    $manifest = @(Get-Manifest $connection)
    $manifest | Export-Csv -LiteralPath (Join-Path $evidenceDir 'file-manifest.csv') -NoTypeInformation -Encoding UTF8
    if ($isReentry) {
        $null = Assert-ReentryState $priorFence $ReentryEvidenceDirectory `
            $ApprovedPriorApplyCommitSha $ApprovedPriorManifestSha256 $current $manifest $runtimeFile
    } else {
        Copy-And-Verify $manifest
    }
    $switchSql = New-DatabaseSwitchSql $manifest
    $rollbackSql = New-DatabaseSwitchSql $manifest -Rollback
    $switchSql | Set-Content -LiteralPath (Join-Path $evidenceDir 'database-switch.sql') -Encoding UTF8
    $rollbackSql | Set-Content -LiteralPath (Join-Path $evidenceDir 'database-rollback.sql') -Encoding UTF8
    $null = Invoke-DevSql $connection $switchSql
    $databaseChanged = $true
    Move-LegacySourcesToTechnicalBackup
    Verify-LegacyBackup $manifest
    $summary.LegacyTopLevelsMoved = @($legacyMoves | ForEach-Object { Split-Path -Leaf $_.Source })
    Set-RuntimeRoots
    Assert-FormalInventory $manifest $false
    $summary.CompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDir 'summary.json') -Encoding UTF8
    $null = New-DevNasHierarchyEvidenceIntegrity $evidenceDir `
        ([string]$current.Sha) $ApprovedApplyCommitSha
    Write-Output "DEV_NAS_BUSINESS_HIERARCHY_FILES_VERIFIED count=$($manifest.Count) bytes=$($summary.TotalBytes)"
} catch {
    $failure = $_
    $rollbackFailures = [Collections.Generic.List[string]]::new()
    try { Restore-LegacySources } catch { $rollbackFailures.Add($_.Exception.Message) }
    if ($databaseChanged) {
        try { $null = Invoke-DevSql $connection (New-DatabaseSwitchSql $manifest -Rollback) }
        catch { $rollbackFailures.Add($_.Exception.Message) }
    }
    if ($runtimeBackup -and (Test-Path -LiteralPath $runtimeBackup)) {
        try { Set-RuntimeRoots -Rollback } catch { $rollbackFailures.Add($_.Exception.Message) }
    }
    if ($transitionActivated -and (Test-Path -LiteralPath $transitionFile)) {
        try {
            $transition.Status = 'RolledBack'
            $transition | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($transitionFile + '.tmp') -Encoding UTF8
            Move-Item -LiteralPath ($transitionFile + '.tmp') -Destination $transitionFile -Force
        } catch { $rollbackFailures.Add($_.Exception.Message) }
    }
    if ($rollbackFailures.Count) {
        throw "Reconciliation failed: $($failure.Exception.Message); rollback failures: $($rollbackFailures -join '; ')"
    }
    throw $failure
} finally {
    if ($lock) { $lock.Dispose(); $lock = $null }
    if ($watchLock) { $watchLock.Dispose(); $watchLock = $null }
    Start-ScheduledTask -TaskName $watcherTask -ErrorAction SilentlyContinue
}
if ((Get-SingleListenerPid 5010) -ne $productionPid) {
    throw 'Production 5010 PID changed during DEV NAS hierarchy reconciliation.'
}
Write-Output "DEV_NAS_BUSINESS_HIERARCHY_COMPLETE evidence=$evidenceDir"
