[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 3)][int] $Round,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+$')][string] $RunId,
    [string] $PostgresHost = '127.0.0.1',
    [int] $PostgresPort = 5432,
    [string] $PostgresUser = 'postgres',
    [Parameter(Mandatory = $true)][string] $PostgresPassword,
    [string] $ExpectedClusterSystemId,
    [switch] $AllowEphemeralGitHubService,
    [string] $PsqlPath = 'psql',
    [string] $ResultsRoot = 'artifacts/km-mock-e2e'
)

$ErrorActionPreference = 'Stop'
$project = 'tests/ApprovalFramework.Tests/ApprovalFramework.Tests.csproj'
$roundDirectory = Join-Path $ResultsRoot "round-$Round"
$diagnosticLog = Join-Path $roundDirectory 'diagnostic.log'
New-Item -ItemType Directory -Force -Path $roundDirectory | Out-Null

$databaseBase = "csit_km_mock_e2e_${RunId}_r${Round}"
$minimumExpectedTests = 216
if ($databaseBase.Length -gt 45) { throw "Isolated database prefix is unexpectedly long: $databaseBase" }

$databaseKeys = @(
    'documentlibrary', 'testreport', 'equipment_deletion', 'vendor_deletion',
    'v2_sources', 'metadata', 'analysis'
)
$templateDatabase = "${databaseBase}_template"
$env:PGPASSWORD = $PostgresPassword
if (-not (Get-Command $PsqlPath -ErrorAction SilentlyContinue)) {
    $windowsPsql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
    if (Test-Path -LiteralPath $windowsPsql) { $PsqlPath = $windowsPsql }
    else { throw 'psql was not found. Install the PostgreSQL 16 client or pass -PsqlPath.' }
}

function Invoke-LoggedCommand {
    param([scriptblock] $Command)
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $commandOutput = @(& $Command 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $commandOutput | Out-File -FilePath $diagnosticLog -Append -Encoding utf8
    $commandOutput | Write-Output
    if ($nativeExitCode -ne 0) { throw "Command failed with exit code $nativeExitCode" }
}

function Invoke-Psql {
    param([string] $Sql)
    Invoke-LoggedCommand {
        & $PsqlPath -X -h $PostgresHost -p $PostgresPort -U $PostgresUser -d postgres `
            -v ON_ERROR_STOP=1 -c $Sql
    }
}

function Invoke-PsqlScalar {
    param([string] $Sql)
    $output = @(& $PsqlPath -X -tA -h $PostgresHost -p $PostgresPort -U $PostgresUser `
        -d postgres -v ON_ERROR_STOP=1 -c $Sql 2>&1)
    $nativeExitCode = $LASTEXITCODE
    $output | Out-File -FilePath $diagnosticLog -Append -Encoding utf8
    if ($nativeExitCode -ne 0) { throw "PostgreSQL identity query failed with exit code $nativeExitCode" }
    return (($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join "`n")
}

try {
    "KM Mock E2E round $Round; run $RunId; commit $env:GITHUB_SHA" |
        Set-Content -LiteralPath $diagnosticLog -Encoding utf8

    if ($PostgresHost -notin @('127.0.0.1', 'localhost', '::1')) {
        throw "PostgreSQL must be loopback-only; refusing host $PostgresHost."
    }
    if ($PostgresUser -ne 'postgres') { throw 'The disposable PostgreSQL service must use its isolated postgres owner.' }

    $clusterSystemId = Invoke-PsqlScalar 'SELECT system_identifier::text FROM pg_control_system();'
    $serverMajor = Invoke-PsqlScalar "SELECT current_setting('server_version_num')::int / 10000;"
    $databaseInventory = @(Invoke-PsqlScalar "SELECT datname FROM pg_database ORDER BY datname;" -split "`n")
    if ($serverMajor -ne '16') { throw "PostgreSQL 16 is required; connected server major is $serverMajor." }
    $forbiddenDatabase = @($databaseInventory | Where-Object {
        $_ -match '(?i)(^|_)(dev|prod|production)(_|$)' -or $_ -eq 'sit_department_system'
    } | Select-Object -First 1)
    if ($forbiddenDatabase.Count -gt 0) {
        throw "Refusing a cluster containing a DEV/Production-like database: $($forbiddenDatabase[0])."
    }

    if ($AllowEphemeralGitHubService) {
        if ($env:GITHUB_ACTIONS -ne 'true') { throw '-AllowEphemeralGitHubService is valid only inside GitHub Actions.' }
        $runDatabasePattern = "^csit_km_mock_e2e_${RunId}_r[1-3]_[a-z0-9_]+$"
        $unexpectedDatabase = @($databaseInventory | Where-Object {
            $_ -notin @('postgres', 'template0', 'template1') -and $_ -notmatch $runDatabasePattern
        })
        if ($unexpectedDatabase.Count -gt 0) {
            throw "GitHub PostgreSQL service is not fresh; unexpected database(s): $($unexpectedDatabase -join ', ')."
        }
    }
    elseif ([string]::IsNullOrWhiteSpace($ExpectedClusterSystemId)) {
        throw 'Local execution requires -ExpectedClusterSystemId from the independently verified test cluster.'
    }
    elseif ($clusterSystemId -ne $ExpectedClusterSystemId) {
        throw "PostgreSQL cluster identity mismatch. Expected $ExpectedClusterSystemId, received $clusterSystemId."
    }
    "Confirmed PostgreSQL 16 cluster $clusterSystemId on ${PostgresHost}:$PostgresPort; databases: $($databaseInventory -join ', ')" |
        Out-File -FilePath $diagnosticLog -Append -Encoding utf8

    $allDatabases = @($templateDatabase) + @($databaseKeys | ForEach-Object { "${databaseBase}_$_" })
    foreach ($database in $allDatabases) {
        if ($database -notmatch '^csit_km_mock_e2e_[0-9]+_r[1-3]_[a-z0-9_]+$') {
            throw "Refusing to manage a database outside the isolated naming convention: $database"
        }
        Invoke-Psql "DROP DATABASE IF EXISTS $database WITH (FORCE);"
    }

    # Historical migrations grant narrowly scoped permissions to this runtime role.
    # It is a NOLOGIN role inside the disposable PostgreSQL service, never a DEV identity.
    Invoke-Psql "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='csit_runtime_dev') THEN CREATE ROLE csit_runtime_dev NOLOGIN; END IF; END `$`$;"
    Invoke-Psql "CREATE DATABASE $templateDatabase;"
    $baselineSql = Join-Path $PSScriptRoot 'baseline-a2d6224.sql'
    if (-not (Test-Path -LiteralPath $baselineSql)) { throw "Missing baseline schema: $baselineSql" }
    Invoke-LoggedCommand {
        & $PsqlPath -X -h $PostgresHost -p $PostgresPort -U $PostgresUser `
            -d $templateDatabase -v ON_ERROR_STOP=1 -f $baselineSql
    }
    $templateConnection = "Host=$PostgresHost;Port=$PostgresPort;Database=$templateDatabase;Username=$PostgresUser;Password=$PostgresPassword;Include Error Detail=true"
    Invoke-LoggedCommand {
        & dotnet ef database update --project SIT.DepartmentSystem.Web.csproj --configuration Release `
            --connection $templateConnection --no-build
    }

    foreach ($key in @($databaseKeys | Where-Object { $_ -notin @('v2_sources', 'analysis') })) {
        Invoke-Psql "CREATE DATABASE ${databaseBase}_$key TEMPLATE $templateDatabase;"
    }
    # This existing test intentionally verifies EnsureCreated against a truly empty DB.
    Invoke-Psql "CREATE DATABASE ${databaseBase}_v2_sources;"
    # The analysis migration test verifies its own Up transition and schema delta.
    Invoke-Psql "CREATE DATABASE ${databaseBase}_analysis TEMPLATE $templateDatabase;"
    $analysisConnection = "Host=$PostgresHost;Port=$PostgresPort;Database=${databaseBase}_analysis;Username=$PostgresUser;Password=$PostgresPassword;Include Error Detail=true"
    Invoke-LoggedCommand {
        & dotnet ef database update 20261102075735_AddDocumentLibraryFileDeletionLifecycle `
            --project SIT.DepartmentSystem.Web.csproj --configuration Release --connection $analysisConnection --no-build
    }

    $connectionByKey = @{}
    foreach ($key in $databaseKeys) {
        $connectionByKey[$key] = "Host=$PostgresHost;Port=$PostgresPort;Database=${databaseBase}_$key;Username=$PostgresUser;Password=$PostgresPassword;Include Error Detail=true"
    }

    $env:CSIT_KM_MOCK_E2E_REQUIRED = 'true'
    $env:CSIT_KM_MOCK_E2E_RUN_ID = $RunId
    $env:CSIT_KM_MOCK_E2E_CLUSTER_ID = $clusterSystemId
    $env:CSIT_DOCUMENTLIBRARY_KM_POSTGRES_CONNECTION = $connectionByKey.documentlibrary
    $env:CSIT_TESTREPORT_KM_POSTGRES_CONNECTION = $connectionByKey.testreport
    $env:CSIT_EQUIPMENT_DELETION_POSTGRES_CONNECTION = $connectionByKey.equipment_deletion
    $env:CSIT_TASK_D_POSTGRES_CONNECTION = $connectionByKey.vendor_deletion
    $env:CSIT_KM_V2_POSTGRES_CONNECTION = $connectionByKey.v2_sources
    $env:CSIT_TASKC_POSTGRES_CONNECTION = $connectionByKey.metadata
    $env:CSIT_KM_ANALYSIS_POSTGRES_CONNECTION = $connectionByKey.analysis

    # Runtime dispatch remains disabled. Individual tests explicitly construct mock-only options.
    $env:KmIntegration__Enabled = 'false'
    $env:KmIntegration__VendorEnabled = 'false'
    $env:KmIntegration__EquipmentEnabled = 'false'
    $env:KmDeletion__Enabled = 'false'
    $env:KmDeletion__VendorEnabled = 'false'
    $env:KmDeletion__EquipmentEnabled = 'false'

    $classes = @(
        'CanonicalKmContractTests', 'KmIntegrationTests', 'KmV2DocumentJobClientTests',
        'KmV2PostgreSqlClaimTests', 'TestReportKmEndToEndTests',
        'DocumentLibraryKmIntegrationTests', 'KmAnalysisIntegrationTests',
        'KmMetadataChangeTests', 'KmTestReportCorrelationTests',
        'EquipmentDocumentKmLifecycleTests', 'VendorAttachmentLifecycleTests',
        'EquipmentFileKmDeletionTests', 'EquipmentSafeDeletionTests',
        'DocumentLibraryFileDeletionTests', 'KmDeletionContractTests', 'KmQueueMonitorTests'
    )
    $filter = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    Invoke-LoggedCommand {
        & dotnet test $project -c Release --no-build --no-restore `
            --filter $filter `
            --logger "trx;LogFileName=km-mock-e2e-round-$Round.trx" `
            --results-directory $roundDirectory `
            --collect 'XPlat Code Coverage'
    }

    $trxPath = Join-Path $roundDirectory "km-mock-e2e-round-$Round.trx"
    if (-not (Test-Path -LiteralPath $trxPath)) { throw "VSTest did not produce the required TRX: $trxPath" }
    [xml]$trxXml = Get-Content -LiteralPath $trxPath -Raw
    $trxResults = @($trxXml.SelectNodes("//*[local-name()='UnitTestResult']"))
    $passedCount = @($trxResults | Where-Object outcome -eq 'Passed').Count
    $failedCount = @($trxResults | Where-Object outcome -eq 'Failed').Count
    $notRunCount = @($trxResults | Where-Object outcome -ne 'Passed').Count - $failedCount
    if ($trxResults.Count -lt $minimumExpectedTests) {
        throw "Incomplete KM Mock E2E selection: expected at least $minimumExpectedTests tests, found $($trxResults.Count)."
    }
    if ($failedCount -gt 0 -or $notRunCount -gt 0 -or $passedCount -ne $trxResults.Count) {
        throw "Incomplete KM Mock E2E execution: $passedCount passed, $failedCount failed, $notRunCount not run/skipped."
    }

    # VSTest can materialize the same collector attachment twice. Keep one
    # byte-identical copy so the artifact remains compact without losing evidence.
    $coverageHashes = @{}
    foreach ($coverageFile in @(Get-ChildItem $roundDirectory -Filter 'coverage.cobertura.xml' -Recurse)) {
        $hash = (Get-FileHash -LiteralPath $coverageFile.FullName -Algorithm SHA256).Hash
        if ($coverageHashes.ContainsKey($hash)) {
            Remove-Item -LiteralPath $coverageFile.FullName -Force
        }
        else { $coverageHashes[$hash] = $coverageFile.FullName }
    }

    [ordered]@{
        round = $Round
        runId = $RunId
        commit = $env:GITHUB_SHA
        exitCode = 0
        completedAtUtc = [DateTime]::UtcNow.ToString('O')
        databases = $databaseKeys.Count
        clusterSystemId = $clusterSystemId
        passed = $passedCount
        failed = $failedCount
        notRun = $notRunCount
    } | ConvertTo-Json | Set-Content (Join-Path $roundDirectory 'round-status.json') -Encoding utf8
}
catch {
    [ordered]@{
        round = $Round
        runId = $RunId
        commit = $env:GITHUB_SHA
        exitCode = 1
        completedAtUtc = [DateTime]::UtcNow.ToString('O')
        error = $_.Exception.Message
    } | ConvertTo-Json | Set-Content (Join-Path $roundDirectory 'round-status.json') -Encoding utf8
    $_ | Out-String | Out-File -FilePath $diagnosticLog -Append -Encoding utf8
    $_ | Out-String | Write-Output
    exit 1
}
