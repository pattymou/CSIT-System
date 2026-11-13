[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ApprovedSourceSha
)

$ErrorActionPreference = 'Stop'
$devRoot = 'C:\ProgramData\CSIT-DEV'
$maintenanceRoot = Join-Path $devRoot 'maintenance'
$runtimeFile = Join-Path $devRoot 'secrets\runtime.json'
$currentFile = Join-Path $devRoot 'state\current.json'
$lockPath = Join-Path $devRoot 'state\deploy.lock'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$watcherTask = 'CSIT DEV Ready Watcher'
$formalRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'
$legacyTopLevels = @('DocumentLibrary','RawData','Assets','ThreeLevel','TestReports')
$lock = $null
$databaseChanged = $false
$filesystemChanged = $false
$runtimeChanged = $false

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Only a local Administrator may roll back the DEV NAS hierarchy.'
    }
}

function Assert-Within([string] $Path, [string] $Root) {
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($Path).StartsWith(
            $parent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside its approved root: $Path"
    }
}

function Get-SingleListenerPid([int] $Port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -ne 1) { throw "Port $Port must have exactly one listener." }
    return [int]$listeners[0]
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
        $result = @($Sql | & $psql -X -w -q -v ON_ERROR_STOP=1 `
            -h ([string]$Connection['Host']) -p ([string]$Connection['Port']) `
            -U ([string]$Connection['Username']) -d ([string]$Connection['Database']) 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('DEV rollback database operation failed: ' + ($result -join ' ')) }
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Move-DirectorySet([string] $FromRoot, [string] $ToRoot, [string[]] $Names) {
    New-Item -ItemType Directory -Path $ToRoot -Force | Out-Null
    foreach ($name in $Names) {
        $source = Join-Path $FromRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
        $target = Join-Path $ToRoot $name
        if (Test-Path -LiteralPath $target) { throw "Rollback directory target already exists: $target" }
        Move-Item -LiteralPath $source -Destination $target
    }
}

Assert-Administrator
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
Assert-Within $evidence $maintenanceRoot
if ((Split-Path -Leaf $evidence) -notlike 'nas-business-hierarchy-*') {
    throw 'EvidenceDirectory is not a DEV NAS hierarchy evidence directory.'
}
$required = @('summary.json','file-manifest.csv','database-switch.sql','database-rollback.sql','runtime.before.json')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $evidence $name) -PathType Leaf)) {
        throw "Rollback evidence is incomplete: $name"
    }
}
$summary = Get-Content -LiteralPath (Join-Path $evidence 'summary.json') -Raw | ConvertFrom-Json
if ([string]$summary.SourceRuntimeSha -ne $ApprovedSourceSha -or
    [string]$summary.NasRoot -ne $formalRoot -or -not $summary.OriginalsRetained) {
    throw 'Rollback evidence does not match the approved source and formal root.'
}
$legacyBackup = [IO.Path]::GetFullPath([string]$summary.LegacySourceBackup)
if ($legacyBackup.StartsWith([IO.Path]::GetFullPath($formalRoot).TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Legacy source backup is inside formal business storage.'
}
$legacyNames = @([string[]]$summary.LegacyTopLevelsMoved)
if ($legacyNames.Count -eq 0 -or @($legacyNames | Where-Object { $_ -notin $legacyTopLevels }).Count) {
    throw 'Rollback evidence has an invalid retained legacy directory set.'
}
$manifest = @(Import-Csv -LiteralPath (Join-Path $evidence 'file-manifest.csv'))
$expectedTargets = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($item in $manifest) {
    $target = [IO.Path]::GetFullPath([string]$item.TargetPath)
    $null = $expectedTargets.Add($target)
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Rollback target is missing: $target"
    }
    $file = Get-Item -LiteralPath $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne [long]$item.Size -or $hash -ne ([string]$item.Sha256).ToLowerInvariant()) {
        throw "Rollback target SHA256 verification failed: $target"
    }
}
$businessTopLevels = @($manifest | ForEach-Object {
    $target = [IO.Path]::GetFullPath([string]$_.TargetPath)
    $prefix = [IO.Path]::GetFullPath($formalRoot).TrimEnd('\') + '\'
    if ($target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        ($target.Substring($prefix.Length) -split '[\\/]')[0]
    }
} | Where-Object { $_ } | Sort-Object -Unique)
$inventoryRoots = @($businessTopLevels | ForEach-Object { Join-Path $formalRoot $_ }) +
    @(Join-Path ([string]$summary.TechnicalRoot) 'RawData')
foreach ($root in $inventoryRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        if (-not $expectedTargets.Contains([IO.Path]::GetFullPath($file.FullName))) {
            throw "Rollback is blocked because a post-reconciliation file exists: $($file.FullName)"
        }
    }
}
foreach ($name in $legacyNames) {
    if (Test-Path -LiteralPath (Join-Path $formalRoot $name)) {
        throw "Legacy source path already exists; rollback stopped: $name"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $legacyBackup $name) -PathType Container)) {
        throw "Retained legacy source is missing: $name"
    }
}

$productionPid = Get-SingleListenerPid 5010
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
if ([string]$current.Sha -ne $ApprovedSourceSha) {
    throw 'The running DEV commit does not match the rollback evidence.'
}
$rollbackRetained = Join-Path (Split-Path -Parent $legacyBackup) `
    ("RollbackRetainedTargets\" + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
$connection = Get-Connection
$runtimeTargetBackup = Join-Path $evidence 'runtime.rollback-target.json'
Copy-Item -LiteralPath $runtimeFile -Destination $runtimeTargetBackup -Force
try {
    $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$current.Pid)"
    if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
        $process.ExecutablePath -notlike "*$($current.Sha)*") {
        throw 'The recorded DEV process is not the current SHA-scoped runtime.'
    }
    Stop-Process -Id ([int]$current.Pid) -Force
    $stopped = Get-Process -Id ([int]$current.Pid) -ErrorAction SilentlyContinue
    if ($stopped) { $null = $stopped.WaitForExit(15000) }

    $filesystemChanged = $true
    Move-DirectorySet $formalRoot $rollbackRetained $businessTopLevels
    Move-DirectorySet $legacyBackup $formalRoot $legacyNames
    Invoke-DevSql $connection (Get-Content -LiteralPath (Join-Path $evidence 'database-rollback.sql') -Raw)
    $databaseChanged = $true
    Copy-Item -LiteralPath (Join-Path $evidence 'runtime.before.json') -Destination $runtimeFile -Force
    $runtimeChanged = $true
    if ((Get-SingleListenerPid 5010) -ne $productionPid) {
        throw 'Production 5010 PID changed during DEV NAS hierarchy rollback.'
    }
    Write-Output "DEV_NAS_BUSINESS_HIERARCHY_ROLLBACK_COMPLETE retained=$rollbackRetained"
} catch {
    $failure = $_
    $recoveryFailures = [Collections.Generic.List[string]]::new()
    if ($runtimeChanged) {
        try { Copy-Item -LiteralPath $runtimeTargetBackup -Destination $runtimeFile -Force }
        catch { $recoveryFailures.Add($_.Exception.Message) }
    }
    if ($databaseChanged) {
        try { Invoke-DevSql $connection (Get-Content -LiteralPath (Join-Path $evidence 'database-switch.sql') -Raw) }
        catch { $recoveryFailures.Add($_.Exception.Message) }
    }
    if ($filesystemChanged) {
        try { Move-DirectorySet $formalRoot $legacyBackup $legacyNames }
        catch { $recoveryFailures.Add($_.Exception.Message) }
        try { Move-DirectorySet $rollbackRetained $formalRoot $businessTopLevels }
        catch { $recoveryFailures.Add($_.Exception.Message) }
    }
    if ($recoveryFailures.Count) {
        throw "Rollback failed: $($failure.Exception.Message); recovery failures: $($recoveryFailures -join '; ')"
    }
    throw $failure
} finally {
    if ($lock) { $lock.Dispose() }
    Start-ScheduledTask -TaskName $watcherTask -ErrorAction SilentlyContinue
}
