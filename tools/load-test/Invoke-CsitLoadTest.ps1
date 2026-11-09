[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{6,32}$')][string] $RunId,
    [ValidateSet('5','20','50','all')][string] $UserLevel = 'all',
    [ValidateSet('mixed','documents','reservations','concurrency')][string] $Workload = 'mixed',
    [string] $PostgresHost = '127.0.0.1',
    [int] $PostgresPort = 5432,
    [string] $PostgresUser = 'postgres',
    [Parameter(Mandatory)][string] $PostgresPassword,
    [string] $ExpectedClusterSystemId,
    [switch] $AllowEphemeralGitHubService,
    [string] $PsqlPath = 'psql',
    [string] $K6Path = 'k6',
    [string] $ResultsRoot = 'artifacts/load-test',
    [string] $Warmup = '5s',
    [string] $Steady = '20s'
)

$ErrorActionPreference = 'Stop'
$database = "csit_load_test_$RunId"
$baseUrl = 'http://127.0.0.1:5088'
$levels = if ($UserLevel -eq 'all') { @(5, 20, 50) } else { @([int]$UserLevel) }
$appProcess = $null
$databaseCreated = $false
$env:PGPASSWORD = $PostgresPassword
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$results = [IO.Path]::GetFullPath((Join-Path $root $ResultsRoot))
$diagnostic = Join-Path $results 'diagnostic.log'
$allowedRoot = Join-Path ([IO.Path]::GetTempPath()) "csit-load-$RunId"
$storageRoot = Join-Path $allowedRoot 'storage'
$script:connectionExhaustionEvents = 0

New-Item -ItemType Directory -Force -Path $results, $storageRoot,
    (Join-Path $storageRoot 'documents'), (Join-Path $storageRoot 'staging'),
    (Join-Path $storageRoot 'raw'), (Join-Path $storageRoot 'assets'),
    (Join-Path $storageRoot 'approval-staging') | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $storageRoot ".csit-load-test-$RunId") | Out-Null
"CSIT load test $RunId; levels=$($levels -join ','); workload=$Workload; commit=$env:GITHUB_SHA" |
    Set-Content -LiteralPath $diagnostic -Encoding utf8

if (-not (Get-Command $PsqlPath -ErrorAction SilentlyContinue)) {
    $windowsPsql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
    if (Test-Path -LiteralPath $windowsPsql) { $PsqlPath = $windowsPsql }
    else { throw 'psql was not found. Install the PostgreSQL 16 client or pass -PsqlPath.' }
}
if (-not (Get-Command $K6Path -ErrorAction SilentlyContinue)) { throw 'k6 was not found.' }
if ($PostgresHost -notin @('127.0.0.1','localhost','::1')) {
    throw "PostgreSQL must be loopback-only; refusing $PostgresHost."
}
if ($PostgresUser -ne 'postgres') { throw 'The disposable PostgreSQL owner must be postgres.' }

function Invoke-PsqlScalar([string] $Sql, [string] $TargetDatabase = 'postgres') {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $PsqlPath -X -tA -h $PostgresHost -p $PostgresPort -U $PostgresUser `
            -d $TargetDatabase -v ON_ERROR_STOP=1 -c $Sql 2>&1)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
    $output | Out-File -LiteralPath $diagnostic -Append -Encoding utf8
    if (($output -join "`n") -match '(?i)too many clients already|remaining connection slots are reserved') {
        $script:connectionExhaustionEvents++
    }
    if ($code -ne 0) { throw "PostgreSQL command failed: $Sql" }
    return (($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join "`n")
}
function Invoke-PsqlScalarWithRetry([string] $Sql, [string] $TargetDatabase, [int] $Attempts = 4) {
    foreach ($attempt in 1..$Attempts) {
        try { return Invoke-PsqlScalar $Sql $TargetDatabase }
        catch {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}
function Start-ConnectionSampler([string] $OutputPath, [string] $StopPath, [string] $TargetDatabase) {
    Remove-Item -LiteralPath $OutputPath, $StopPath -Force -ErrorAction SilentlyContinue
    return Start-Job -ScriptBlock {
        param($Psql, $HostName, $Port, $User, $Password, $Database, $Output, $Stop)
        $env:PGPASSWORD = $Password
        $peakTotal = 0; $peakDatabase = 0; $samples = 0; $sampleFailures = 0; $exhaustions = 0
        while (-not (Test-Path -LiteralPath $Stop)) {
            $result = @(& $Psql -X -tA -h $HostName -p $Port -U $User -d $Database `
                -v ON_ERROR_STOP=1 -c "SELECT count(*)::text || '|' || (count(*) FILTER (WHERE datname=current_database()))::text FROM pg_stat_activity;" 2>&1)
            if ($LASTEXITCODE -eq 0) {
                $value = ($result | ForEach-Object { $_.ToString().Trim() } |
                    Where-Object { $_ -match '^\d+\|\d+$' } | Select-Object -Last 1)
                if ($value) {
                    $parts = $value -split '\|'; $samples++
                    $peakTotal = [Math]::Max($peakTotal, [int]$parts[0])
                    $peakDatabase = [Math]::Max($peakDatabase, [int]$parts[1])
                } else { $sampleFailures++ }
            } else {
                $sampleFailures++
                if (($result -join "`n") -match '(?i)too many clients already|remaining connection slots are reserved') {
                    $exhaustions++
                }
            }
            Start-Sleep -Milliseconds 200
        }
        [ordered]@{ samples=$samples; sampleFailures=$sampleFailures; peakConnections=$peakTotal;
            peakDatabaseConnections=$peakDatabase; connectionExhaustionEvents=$exhaustions } |
            ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
    } -ArgumentList $PsqlPath, $PostgresHost, $PostgresPort, $PostgresUser, $PostgresPassword,
        $TargetDatabase, $OutputPath, $StopPath
}
function Stop-ConnectionSampler($Job, [string] $StopPath) {
    New-Item -ItemType File -Force -Path $StopPath | Out-Null
    Wait-Job -Job $Job -Timeout 10 | Out-Null
    if ($Job.State -eq 'Running') { Stop-Job -Job $Job }
    Receive-Job -Job $Job 2>&1 | Out-File -LiteralPath $diagnostic -Append -Encoding utf8
    Remove-Job -Job $Job -Force
}
function Get-AppConnectionExhaustionCount([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    return @(Select-String -LiteralPath $Path -Pattern 'too many clients already|remaining connection slots are reserved').Count
}
function Invoke-Logged([scriptblock] $Command) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $output = @(& $Command 2>&1); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $old }
    $output | Tee-Object -FilePath $diagnostic -Append | Write-Output
    if ($code -ne 0) { throw "Command failed with exit code $code." }
}
function New-IdentityHeader([string] $Role, [int] $Index = 1,
    [string] $Scope = 'CsitStaff', [string] $Access = 'Editor') {
    $account = "load-$RunId-$($Role.ToLowerInvariant())-$Index"
    $canonical = "$account|$Role|$Scope|$Access|DA40|$Role $Index|$RunId"
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($env:CSIT_LOAD_IDENTITY_SECRET))
    try { $signature = ([BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))) -replace '-', '').ToLowerInvariant() }
    finally { $hmac.Dispose() }
    return "$canonical|$signature"
}

try {
    $clusterId = Invoke-PsqlScalar 'SELECT system_identifier::text FROM pg_control_system();'
    $serverMajor = Invoke-PsqlScalar "SELECT current_setting('server_version_num')::int / 10000;"
    $serverMaxConnections = [int](Invoke-PsqlScalar "SELECT current_setting('max_connections');")
    $reservedConnections = [int](Invoke-PsqlScalar "SELECT current_setting('superuser_reserved_connections');")
    $applicationPoolMaximum = [Math]::Min(80, $serverMaxConnections - $reservedConnections - 10)
    if ($applicationPoolMaximum -lt 10 -or
        $applicationPoolMaximum -ge ($serverMaxConnections - $reservedConnections)) {
        throw "PostgreSQL connection capacity is insufficient for an isolated load test."
    }
    $inventory = @((Invoke-PsqlScalar 'SELECT datname FROM pg_database ORDER BY datname;') -split '\r?\n')
    if ($serverMajor -ne '16') { throw "PostgreSQL 16 is required; found $serverMajor." }
    $forbidden = @($inventory | Where-Object { $_ -eq 'sit_department_system' -or
        $_ -match '(?i)(^|_)(dev|prod|production)(_|$)' } | Select-Object -First 1)
    if ($forbidden.Count) { throw "Refusing cluster with DEV/Production-like database $($forbidden[0])." }
    if ($AllowEphemeralGitHubService) {
        if ($env:GITHUB_ACTIONS -ne 'true') { throw '-AllowEphemeralGitHubService is GitHub Actions-only.' }
        $unexpected = @($inventory | Where-Object { $_ -notin @('postgres','template0','template1') })
        if ($unexpected.Count) { throw "GitHub PostgreSQL service is not fresh: $($unexpected -join ', ')." }
    }
    elseif ([string]::IsNullOrWhiteSpace($ExpectedClusterSystemId) -or $clusterId -ne $ExpectedClusterSystemId) {
        throw 'Local execution requires the exact independently verified -ExpectedClusterSystemId.'
    }

    Invoke-PsqlScalar "DROP DATABASE IF EXISTS $database WITH (FORCE);" | Out-Null
    Invoke-PsqlScalar "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='csit_runtime_dev') THEN CREATE ROLE csit_runtime_dev NOLOGIN; END IF; END `$`$;" | Out-Null
    Invoke-PsqlScalar "CREATE DATABASE $database;" | Out-Null
    $databaseCreated = $true
    $baseline = Join-Path $root 'tools/km-mock-e2e/baseline-a2d6224.psql'
    Invoke-Logged { & $PsqlPath -X -h $PostgresHost -p $PostgresPort -U $PostgresUser -d $database -v ON_ERROR_STOP=1 -f $baseline }
    $connection = "Host=$PostgresHost;Port=$PostgresPort;Database=$database;Username=$PostgresUser;Password=$PostgresPassword;Include Error Detail=true;Maximum Pool Size=$applicationPoolMaximum"
    Invoke-Logged { & dotnet ef database update --project (Join-Path $root 'SIT.DepartmentSystem.Web.csproj') `
        --configuration Release --connection $connection --no-build }

    $env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
    $env:ASPNETCORE_URLS = $baseUrl
    $env:AuthenticationMode = 'Legacy'
    $env:ConnectionStrings__Postgres = $connection
    $env:LoadTest__Required = 'true'
    $env:LoadTest__RunId = $RunId
    $env:LoadTest__Workload = $Workload
    $env:LoadTest__ExpectedClusterSystemId = $clusterId
    $env:LoadTest__AllowedRoot = $allowedRoot
    $env:LoadTest__StorageRoot = $storageRoot
    $secretBytes = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($secretBytes) } finally { $rng.Dispose() }
    $env:CSIT_LOAD_IDENTITY_SECRET = ([BitConverter]::ToString($secretBytes) -replace '-', '').ToLowerInvariant()
    $env:LoadTest__IdentitySecret = $env:CSIT_LOAD_IDENTITY_SECRET
    $env:DocumentStorage__RootPath = Join-Path $storageRoot 'documents'
    $env:DocumentStaging__RootPath = Join-Path $storageRoot 'staging'
    $env:RawData__Enabled = 'false'
    $env:RawData__NasRootPath = Join-Path $storageRoot 'raw'
    $env:KmIntegration__Enabled = 'false'
    $env:KmIntegration__VendorEnabled = 'false'
    $env:KmIntegration__EquipmentEnabled = 'false'
    $env:KmDeletion__Enabled = 'false'
    $env:KmDeletion__VendorEnabled = 'false'
    $env:KmDeletion__EquipmentEnabled = 'false'

    $appOut = Join-Path $results 'app.stdout.log'; $appErr = Join-Path $results 'app.stderr.log'
    $startParameters = @{ FilePath='dotnet'; ArgumentList=@('run','--project',(Join-Path $root 'SIT.DepartmentSystem.Web.csproj'),
        '-c','Release','--no-build','--no-launch-profile'); WorkingDirectory=$root; PassThru=$true;
        RedirectStandardOutput=$appOut; RedirectStandardError=$appErr }
    if ($IsWindows) { $startParameters.WindowStyle = 'Hidden' }
    $appProcess = Start-Process @startParameters
    $adminHeader = New-IdentityHeader -Role Admin
    $ready = $false
    foreach ($attempt in 1..60) {
        if ($appProcess.HasExited) { break }
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/api/load-test-control/health" `
                -Headers @{ 'X-CSIT-Load-Identity' = $adminHeader } -TimeoutSec 2
            if ($health.isolated -eq $true -and $health.runId -eq $RunId) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "The isolated CSIT host did not become ready. See $appErr." }

    foreach ($users in $levels) {
        $levelDirectory = Join-Path $results "level-$users"
        New-Item -ItemType Directory -Force -Path $levelDirectory | Out-Null
        $samplerPath = Join-Path $levelDirectory 'postgres-connection-samples.json'
        $samplerStopPath = Join-Path $levelDirectory '.stop-postgres-sampler'
        $appExhaustionsBefore = Get-AppConnectionExhaustionCount $appErr
        $psqlExhaustionsBefore = $script:connectionExhaustionEvents
        $env:CSIT_LOAD_BASE_URL = $baseUrl
        $env:CSIT_LOAD_RUN_ID = $RunId
        $env:CSIT_LOAD_USERS = $users.ToString()
        $env:CSIT_LOAD_WORKLOAD = $Workload
        $env:CSIT_LOAD_OUTPUT = ($levelDirectory -replace '\\','/')
        $env:CSIT_LOAD_WARMUP = $Warmup
        $env:CSIT_LOAD_STEADY = $Steady
        $sampler = Start-ConnectionSampler $samplerPath $samplerStopPath $database
        try { Invoke-Logged { & $K6Path run (Join-Path $PSScriptRoot 'csit-multi-user.js') } }
        finally { Stop-ConnectionSampler $sampler $samplerStopPath }
        $invariants = Invoke-RestMethod -Uri "$baseUrl/api/load-test-control/invariants" `
            -Headers @{ 'X-CSIT-Load-Identity' = $adminHeader } -TimeoutSec 30
        $invariants | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $levelDirectory 'invariants.json') -Encoding utf8
        if ($invariants.passed -ne $true) { throw "Level $users consistency validation failed." }
        $metricsJson = Invoke-PsqlScalarWithRetry "SELECT json_build_object('connections',numbackends,'total_connections',(SELECT count(*) FROM pg_stat_activity),'transactions',xact_commit+xact_rollback,'deadlocks',deadlocks,'temp_bytes',temp_bytes,'max_connections',current_setting('max_connections')::int,'superuser_reserved_connections',current_setting('superuser_reserved_connections')::int)::text FROM pg_stat_database WHERE datname=current_database();" $database
        $metrics = $metricsJson | ConvertFrom-Json
        $samples = if (Test-Path -LiteralPath $samplerPath) {
            Get-Content -LiteralPath $samplerPath -Raw | ConvertFrom-Json
        } else {
            [pscustomobject]@{ samples=0; sampleFailures=1; peakConnections=0;
                peakDatabaseConnections=0; connectionExhaustionEvents=0 }
        }
        $exhaustions = [int]$samples.connectionExhaustionEvents +
            ($script:connectionExhaustionEvents - $psqlExhaustionsBefore) +
            ((Get-AppConnectionExhaustionCount $appErr) - $appExhaustionsBefore)
        $metrics | Add-Member -NotePropertyName applicationPoolMaximum -NotePropertyValue $applicationPoolMaximum
        $metrics | Add-Member -NotePropertyName peakConnections -NotePropertyValue ([int]$samples.peakConnections)
        $metrics | Add-Member -NotePropertyName peakDatabaseConnections -NotePropertyValue ([int]$samples.peakDatabaseConnections)
        $metrics | Add-Member -NotePropertyName connectionSamples -NotePropertyValue ([int]$samples.samples)
        $metrics | Add-Member -NotePropertyName connectionSampleFailures -NotePropertyValue ([int]$samples.sampleFailures)
        $metrics | Add-Member -NotePropertyName connectionExhaustionEvents -NotePropertyValue $exhaustions
        $metrics | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $levelDirectory 'postgres-metrics.json') -Encoding utf8
    }

    & (Join-Path $PSScriptRoot 'New-CsitLoadTestReport.ps1') -ResultsRoot $results -ExpectedLevels $levels -Workload $Workload -Enforce
}
catch {
    $_ | Out-String | Tee-Object -FilePath $diagnostic -Append | Write-Output
    throw
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        $appProcess.WaitForExit(10000) | Out-Null
    }
    if ($databaseCreated -and $database -match '^csit_load_test_[a-z0-9]{6,32}$') {
        try { Invoke-PsqlScalar "DROP DATABASE IF EXISTS $database WITH (FORCE);" | Out-Null }
        catch { $_ | Out-String | Out-File -LiteralPath $diagnostic -Append -Encoding utf8 }
    }
    if (Test-Path -LiteralPath $allowedRoot) { Remove-Item -LiteralPath $allowedRoot -Recurse -Force }
    Remove-Item Env:CSIT_LOAD_IDENTITY_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:LoadTest__IdentitySecret -ErrorAction SilentlyContinue
}
