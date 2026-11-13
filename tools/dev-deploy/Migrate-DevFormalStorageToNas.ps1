[CmdletBinding(DefaultParameterSetName = 'Audit')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [switch] $Apply,
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ApprovedSourceSha
)

$ErrorActionPreference = 'Stop'
$devRoot = 'C:\ProgramData\CSIT-DEV'
$runtimeFile = Join-Path $devRoot 'secrets\runtime.json'
$currentFile = Join-Path $devRoot 'state\current.json'
$maintenanceRoot = Join-Path $devRoot 'maintenance'
$lockPath = Join-Path $devRoot 'state\deploy.lock'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$watcherTask = 'CSIT DEV Ready Watcher'
$nasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'
$oldDocumentRoot = 'C:\ProgramData\CSIT-DEV\documents'
$oldStorageRoot = 'D:\CSIT_Dev_Data'
$newDocumentRoot = Join-Path $nasRoot 'DocumentLibrary'
$newRawRoot = Join-Path $nasRoot 'RawData'
$lock = $null
$productionPid = 0
$oldDevPid = 0
$runtimeBackup = $null
$databaseChanged = $false

$rootMappings = [ordered]@{
    $oldDocumentRoot = $newDocumentRoot
    (Join-Path $oldStorageRoot 'Assets\equipment') = (Join-Path $nasRoot 'Assets\equipment')
    (Join-Path $oldStorageRoot 'Assets\goods') = (Join-Path $nasRoot 'Assets\goods')
    (Join-Path $oldStorageRoot 'ThreeLevel\certification') = (Join-Path $nasRoot 'ThreeLevel\certification')
    (Join-Path $oldStorageRoot 'ThreeLevel\verification') = (Join-Path $nasRoot 'ThreeLevel\verification')
    (Join-Path $oldStorageRoot 'TestReports') = (Join-Path $nasRoot 'TestReports')
    (Join-Path $oldStorageRoot 'RawData') = $newRawRoot
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Only a local Administrator may run the DEV formal-storage migration.'
    }
}

function Get-SingleListenerPid([int] $Port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -ne 1) { throw "Port $Port must have exactly one listener." }
    return [int] $listeners[0]
}

function Get-Connection {
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    $raw = [string] $runtime.PSObject.Properties['ConnectionStrings:Postgres'].Value
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
    $env:PGPASSWORD = [string] $Connection['Password']
    try {
        $result = @($Sql | & $psql -X -w -q -t -A -F '|' -v ON_ERROR_STOP=1 `
            -h ([string]$Connection['Host']) -p ([string]$Connection['Port']) `
            -U ([string]$Connection['Username']) -d ([string]$Connection['Database']) 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('DEV database operation failed: ' + ($result -join ' ')) }
        return @($result | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Assert-CurrentLocalRoots($Connection) {
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    if ([string]$runtime.PSObject.Properties['DocumentStorage:RootPath'].Value -ne $oldDocumentRoot -or
        [string]$runtime.PSObject.Properties['RawData:NasRootPath'].Value -ne 'D:/CSIT_Dev_Data/RawData') {
        throw 'DEV runtime formal roots are neither the reviewed local source state nor the completed NAS state.'
    }
    $expected = @(
        'AssetRootPath|equipment|D:/CSIT_Dev_Data/Assets/equipment',
        'AssetRootPath|goods|D:/CSIT_Dev_Data/Assets/goods',
        'TestReportRootPath|default|D:/CSIT_Dev_Data/TestReports',
        'ThreeLevelRootPath|certification|D:/CSIT_Dev_Data/ThreeLevel/certification',
        'ThreeLevelRootPath|verification|D:/CSIT_Dev_Data/ThreeLevel/verification')
    $actual = @(Invoke-DevSql $Connection @'
BEGIN TRANSACTION READ ONLY;
SELECT category, name, value FROM system_options
WHERE is_enabled AND category IN ('AssetRootPath','ThreeLevelRootPath','TestReportRootPath')
ORDER BY category,name;
COMMIT;
'@)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw 'DEV SystemOptions formal roots do not match the reviewed migration source state.'
    }
}

function Get-Manifest {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($mapping in $rootMappings.GetEnumerator()) {
        $sourceRoot = [IO.Path]::GetFullPath([string]$mapping.Key).TrimEnd('\')
        $targetRoot = [IO.Path]::GetFullPath([string]$mapping.Value).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
            $sourcePrefix = $sourceRoot + '\'
            if (-not $file.FullName.StartsWith($sourcePrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Source file escaped its approved root: $($file.FullName)"
            }
            $relative = $file.FullName.Substring($sourcePrefix.Length)
            $target = Join-Path $targetRoot $relative
            if (-not $target.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Target file escaped its approved root: $target"
            }
            $rows.Add([pscustomobject]@{
                SourceRoot=$sourceRoot; TargetRoot=$targetRoot; RelativePath=$relative
                SourcePath=$file.FullName; TargetPath=$target; Size=$file.Length
                Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
    }
    return @($rows)
}

function Assert-DatabaseFiles($Connection, $Manifest) {
    $bySource = @{}
    foreach ($item in $Manifest) { $bySource[$item.SourcePath.ToLowerInvariant()] = $item }
    $rows = @(Invoke-DevSql $Connection @'
BEGIN TRANSACTION READ ONLY;
SELECT 'Document', relative_path, sha256, file_size FROM document_files WHERE storage_area='Library';
SELECT 'Vendor', folder_path || E'\\' || stored_file_name, sha256, file_size FROM vendor_files;
SELECT 'Equipment', "FilePath" || E'\\' || "FileName", coalesce(sha256,''), "FileSize" FROM apparatus_files;
SELECT 'Case', file_path || E'\\' || file_name, coalesce(sha256,''), file_size FROM module_case_files;
COMMIT;
'@)
    foreach ($line in $rows) {
        $parts = $line -split '\|', 4
        $path = if ($parts[0] -eq 'Document') {
            Join-Path $oldDocumentRoot $parts[1]
        } else { $parts[1] }
        $key = [IO.Path]::GetFullPath($path).ToLowerInvariant()
        if (-not $bySource.ContainsKey($key)) { throw "Database formal file is absent: $path" }
        $item = $bySource[$key]
        if ([long]$parts[3] -ne [long]$item.Size -or
            (-not [string]::IsNullOrWhiteSpace($parts[2]) -and
             $parts[2] -ne $item.Sha256)) {
            throw "Database size or SHA256 does not match the formal file: $path"
        }
    }
}

function Copy-And-Verify($Manifest) {
    foreach ($targetRoot in @($rootMappings.Values | Sort-Object -Unique)) {
        New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    }
    foreach ($item in $Manifest) {
        $parent = Split-Path -Parent $item.TargetPath
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        if (Test-Path -LiteralPath $item.TargetPath -PathType Leaf) {
            $existing = Get-Item -LiteralPath $item.TargetPath
            if ($existing.Length -ne $item.Size -or
                (Get-FileHash -LiteralPath $item.TargetPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
                    $item.Sha256) {
                throw "Existing NAS target differs and will not be overwritten: $($item.TargetPath)"
            }
        } else {
            Copy-Item -LiteralPath $item.SourcePath -Destination $item.TargetPath
        }
        $target = Get-Item -LiteralPath $item.TargetPath
        $targetHash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($target.Length -ne $item.Size -or $targetHash -ne $item.Sha256) {
            throw "NAS verification failed: $($item.TargetPath)"
        }
    }
}

function Set-RuntimeRoots {
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    $runtime.PSObject.Properties['DocumentStorage:RootPath'].Value = $newDocumentRoot
    $runtime.PSObject.Properties['RawData:NasRootPath'].Value = $newRawRoot
    $temporary = $runtimeFile + '.nas-cutover.tmp'
    $runtime | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding UTF8
    $acl = Get-Acl -LiteralPath $runtimeFile
    Set-Acl -LiteralPath $temporary -AclObject $acl
    Move-Item -LiteralPath $temporary -Destination $runtimeFile -Force
}

function Set-DatabaseRoots($Connection) {
    $null = Invoke-DevSql $Connection @'
BEGIN;
UPDATE system_options SET value='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets\equipment', updated_at=now()
 WHERE category='AssetRootPath' AND lower(name)='equipment' AND value='D:/CSIT_Dev_Data/Assets/equipment' AND is_enabled;
UPDATE system_options SET value='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets\goods', updated_at=now()
 WHERE category='AssetRootPath' AND lower(name)='goods' AND value='D:/CSIT_Dev_Data/Assets/goods' AND is_enabled;
UPDATE system_options SET value='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\ThreeLevel\certification', updated_at=now()
 WHERE category='ThreeLevelRootPath' AND lower(name)='certification' AND value='D:/CSIT_Dev_Data/ThreeLevel/certification' AND is_enabled;
UPDATE system_options SET value='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\ThreeLevel\verification', updated_at=now()
 WHERE category='ThreeLevelRootPath' AND lower(name)='verification' AND value='D:/CSIT_Dev_Data/ThreeLevel/verification' AND is_enabled;
UPDATE system_options SET value='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\TestReports', updated_at=now()
 WHERE category='TestReportRootPath' AND lower(name)='default' AND value='D:/CSIT_Dev_Data/TestReports' AND is_enabled;
UPDATE vendor_files SET folder_path=replace(folder_path,'D:\CSIT_Dev_Data\Assets','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets')
 WHERE left(folder_path,length('D:\CSIT_Dev_Data\Assets'))='D:\CSIT_Dev_Data\Assets';
UPDATE apparatus_files SET "FilePath"=replace("FilePath",'D:\CSIT_Dev_Data\Assets','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets')
 WHERE left("FilePath",length('D:\CSIT_Dev_Data\Assets'))='D:\CSIT_Dev_Data\Assets';
UPDATE module_case_files SET file_path=replace(file_path,'D:\CSIT_Dev_Data','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV')
 WHERE left(file_path,length('D:\CSIT_Dev_Data'))='D:\CSIT_Dev_Data';
UPDATE apparatus_files SET
 nas_folder_path=replace(nas_folder_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData'),
 nas_file_path=replace(nas_file_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData'),
 raw_json_path=replace(raw_json_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData')
 WHERE coalesce(nas_folder_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%' OR
       coalesce(nas_file_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%' OR
       coalesce(raw_json_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%';
UPDATE module_case_files SET
 nas_folder_path=replace(nas_folder_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData'),
 nas_file_path=replace(nas_file_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData'),
 raw_json_path=replace(raw_json_path,'D:\CSIT_Dev_Data\RawData','\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData')
 WHERE coalesce(nas_folder_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%' OR
       coalesce(nas_file_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%' OR
       coalesce(raw_json_path,'') LIKE 'D:\CSIT_Dev_Data\RawData%';
COMMIT;
'@
}

function Restore-DatabaseRoots($Connection) {
    $null = Invoke-DevSql $Connection @'
BEGIN;
UPDATE system_options SET value='D:/CSIT_Dev_Data/Assets/equipment',updated_at=now() WHERE category='AssetRootPath' AND lower(name)='equipment';
UPDATE system_options SET value='D:/CSIT_Dev_Data/Assets/goods',updated_at=now() WHERE category='AssetRootPath' AND lower(name)='goods';
UPDATE system_options SET value='D:/CSIT_Dev_Data/ThreeLevel/certification',updated_at=now() WHERE category='ThreeLevelRootPath' AND lower(name)='certification';
UPDATE system_options SET value='D:/CSIT_Dev_Data/ThreeLevel/verification',updated_at=now() WHERE category='ThreeLevelRootPath' AND lower(name)='verification';
UPDATE system_options SET value='D:/CSIT_Dev_Data/TestReports',updated_at=now() WHERE category='TestReportRootPath' AND lower(name)='default';
UPDATE vendor_files SET folder_path=replace(folder_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets','D:\CSIT_Dev_Data\Assets')
 WHERE left(folder_path,length('\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets'))='\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets';
UPDATE apparatus_files SET "FilePath"=replace("FilePath",'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Assets','D:\CSIT_Dev_Data\Assets');
UPDATE module_case_files SET file_path=replace(file_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV','D:\CSIT_Dev_Data');
UPDATE apparatus_files SET nas_folder_path=replace(nas_folder_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData'),nas_file_path=replace(nas_file_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData'),raw_json_path=replace(raw_json_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData');
UPDATE module_case_files SET nas_folder_path=replace(nas_folder_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData'),nas_file_path=replace(nas_file_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData'),raw_json_path=replace(raw_json_path,'\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData','D:\CSIT_Dev_Data\RawData');
COMMIT;
'@
}

Assert-Administrator
foreach ($path in @($runtimeFile,$currentFile,$psql)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required DEV file is absent: $path" }
}
$productionPid = Get-SingleListenerPid 5010
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
$oldDevPid = [int]$current.Pid
$connection = Get-Connection
Assert-CurrentLocalRoots $connection
$manifest = @(Get-Manifest)
Assert-DatabaseFiles $connection $manifest
$summary = [ordered]@{
    Mode = if ($Apply) { 'Apply' } else { 'Audit' }
    SourceRuntimeSha = [string]$current.Sha
    NasRoot = $nasRoot
    FileCount = $manifest.Count
    TotalBytes = [long](($manifest | Measure-Object Size -Sum).Sum)
    SourceRoots = $rootMappings
    OriginalsRetained = $true
    CompletedKmJobsTouched = $false
    ProductionPid = $productionPid
}
if (-not $Apply) {
    $summary | ConvertTo-Json -Depth 6
    Write-Output 'DEV_NAS_MIGRATION_AUDIT_ONLY'
    exit 0
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$head = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ApprovedSourceSha) {
    throw 'ApprovedSourceSha does not match the reviewed source checkout.'
}
$probe = Join-Path $PSScriptRoot 'Test-DevNasAccess.ps1'
$probeOutput = @(& $probe)
if ($LASTEXITCODE -ne 0 -or $probeOutput -notcontains 'DEV_NAS_ACCESS_PASS') {
    throw 'LOCAL SERVICE NAS access is not verified; migration was not started.'
}

try {
    $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
} catch [IO.IOException] { throw 'A DEV deployment or maintenance operation is already running.' }

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$evidenceDir = Join-Path $maintenanceRoot "nas-cutover-$stamp-$($ApprovedSourceSha.Substring(0,8))"
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$runtimeBackup = Join-Path $evidenceDir 'runtime.before.json'
Copy-Item -LiteralPath $runtimeFile -Destination $runtimeBackup
$manifest | Export-Csv -LiteralPath (Join-Path $evidenceDir 'file-manifest.csv') -NoTypeInformation -Encoding UTF8
$summary.EvidenceDirectory = $evidenceDir
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceDir 'summary.json') -Encoding UTF8

try {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$oldDevPid"
    if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
        $process.ExecutablePath -notlike "*$($current.Sha)*") {
        throw 'The recorded DEV process is not the current SHA-scoped runtime.'
    }
    Stop-Process -Id $oldDevPid -Force
    $stopped = Get-Process -Id $oldDevPid -ErrorAction SilentlyContinue
    if ($stopped) { $null = $stopped.WaitForExit(15000) }
    $manifest = @(Get-Manifest)
    Assert-DatabaseFiles $connection $manifest
    $summary.FileCount = $manifest.Count
    $summary.TotalBytes = [long](($manifest | Measure-Object Size -Sum).Sum)
    $manifest | Export-Csv -LiteralPath (Join-Path $evidenceDir 'file-manifest.csv') -NoTypeInformation -Encoding UTF8
    Copy-And-Verify $manifest
    Set-DatabaseRoots $connection
    $databaseChanged = $true
    Set-RuntimeRoots
    $summary.CompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceDir 'summary.json') -Encoding UTF8
    Write-Output "DEV_NAS_MIGRATION_FILES_VERIFIED count=$($manifest.Count) bytes=$($summary.TotalBytes)"
} catch {
    if ($databaseChanged) { Restore-DatabaseRoots $connection }
    if ($runtimeBackup -and (Test-Path -LiteralPath $runtimeBackup)) {
        Copy-Item -LiteralPath $runtimeBackup -Destination $runtimeFile -Force
    }
    throw
} finally {
    if ($lock) { $lock.Dispose(); $lock = $null }
    Start-ScheduledTask -TaskName $watcherTask -ErrorAction SilentlyContinue
}

if ((Get-SingleListenerPid 5010) -ne $productionPid) {
    throw 'Production 5010 PID changed during DEV NAS migration.'
}
Write-Output "DEV_NAS_MIGRATION_COMPLETE evidence=$evidenceDir"
