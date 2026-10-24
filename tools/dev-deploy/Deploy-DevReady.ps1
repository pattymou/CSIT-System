param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^dev-ready-[0-9]{14}-[0-9a-f]{8}$')]
    [string] $Tag,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Sha,
    [switch] $SimulateTestFailure,
    [switch] $SimulateSmokeFailure
)

$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$gitExe = 'C:\Program Files\Git\cmd\git.exe'
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
$curlExe = 'C:\Windows\System32\curl.exe'
$branch = 'feature/km-stage-integration'
$remote = 'https://github.com/pattymou/CSIT-System.git'
$stateRoot = Join-Path $root 'state'
$releaseRoot = Join-Path $root 'releases'
$workRoot = Join-Path $root 'work'
$secretRoot = Join-Path $root 'secrets'
$reviewRoot = Join-Path $root 'migration-reviews'
$secretFile = Join-Path $secretRoot 'runtime.json'
$baseSettings = Join-Path $secretRoot 'base-appsettings.json'
$currentFile = Join-Path $stateRoot 'current.json'
$localhostCert = '46BFDA36B073576B3116CB5E52D7ABE1FC595F2D'
$publicCert = 'D4E5BA5E6D6252575BB8D0ADD3F2A98B279C503B'
$productionBefore = $null
$lock = $null
$devRuntimeConnection = $null

. (Join-Path $PSScriptRoot 'DevMigrationReview.ps1')

function Assert-Within([string] $Path, [string] $Parent) {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The path is outside the DEV deployment root.'
    }
    if ($Path -match '^[dD]:\\CSIT_Publish') { throw 'Production publish path is forbidden.' }
}

function Get-ProductionPid {
    $connections = @(Get-NetTCPConnection -State Listen -LocalPort 5010 -ErrorAction SilentlyContinue)
    $pids = @($connections | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($pids.Count -ne 1) { throw 'Production 5010 PID guard failed.' }
    return [int] $pids[0]
}

function Get-DevPid {
    $connections = @(Get-NetTCPConnection -State Listen -LocalPort 7296 -ErrorAction SilentlyContinue)
    $pids = @($connections | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($pids.Count -gt 1) { throw 'Multiple DEV 7296 processes detected.' }
    if ($pids.Count -eq 0) { return 0 }
    return [int] $pids[0]
}

function Assert-DevProcess([int] $ProcessId) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
    if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe') {
        throw 'The DEV listener is not the expected application.'
    }
    Assert-Within $process.ExecutablePath $releaseRoot
    return $process
}

function Assert-ProductionUnchanged {
    if ((Get-ProductionPid) -ne $productionBefore) { throw 'Production 5010 PID changed during DEV deployment.' }
    $service = Get-CimInstance Win32_Service -Filter "Name='SIT.DepartmentSystem.Web'"
    if ($null -eq $service -or $service.State -ne 'Running') { throw 'Production service state changed.' }
}

function Assert-DevRuntimeConfig {
    if (-not (Test-Path -LiteralPath $secretFile) -or -not (Test-Path -LiteralPath $baseSettings)) {
        throw 'Restricted DEV runtime configuration is incomplete.'
    }
    $secrets = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
    $raw = $secrets.PSObject.Properties['ConnectionStrings:Postgres'].Value
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    $connection.set_ConnectionString([string] $raw)
    if ([string] $connection['Database'] -ne 'sit_department_system_dev' -or
        [string] $connection['Username'] -ne 'csit_runtime_dev' -or
        -not $connection.ContainsKey('Password')) {
        throw 'The runtime database is not the authorized DEV role and database.'
    }
    $script:devRuntimeConnection = $connection
    $base = Get-Content -LiteralPath $baseSettings -Raw | ConvertFrom-Json
    $baseConnection = [string] $base.ConnectionStrings.Postgres
    if ($baseConnection -match 'sit_department_system(?!_dev)' -or $baseConnection -match '[dD]:\\CSIT_Publish') {
        throw 'The base configuration contains a Production target.'
    }
    $env:PGPASSWORD = [string] $connection['Password']
    try {
        $query = 'SELECT current_database(), current_user;'
        $result = $query | & 'C:\Program Files\PostgreSQL\16\bin\psql.exe' -X -w -t -A -F ',' -v ON_ERROR_STOP=1 `
            -h ([string] $connection['Host']) -p ([string] $connection['Port']) `
            -U ([string] $connection['Username']) -d ([string] $connection['Database'])
        if ($LASTEXITCODE -ne 0 -or ($result -join '').Trim() -ne 'sit_department_system_dev,csit_runtime_dev') {
            throw 'The DEV runtime database connectivity guard failed.'
        }
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Get-AppliedDevMigrations([string[]] $MigrationIds) {
    if ($null -eq $devRuntimeConnection -or $MigrationIds.Count -eq 0) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: DEV migration verification is unavailable.'
    }
    foreach ($migrationId in $MigrationIds) {
        if ($migrationId -notmatch '^[0-9]{14}_[A-Za-z0-9_]+$') {
            throw 'DEV_MIGRATION_REVIEW_INVALID: a Migration ID is malformed.'
        }
    }
    $quoted = @($MigrationIds | ForEach-Object { "'$_'" }) -join ','
    $query = 'SELECT "MigrationId" FROM "__EFMigrationsHistory" WHERE "MigrationId" IN (' +
        $quoted + ') ORDER BY "MigrationId";'
    $env:PGPASSWORD = [string] $devRuntimeConnection['Password']
    try {
        $result = @($query | & 'C:\Program Files\PostgreSQL\16\bin\psql.exe' -X -w -t -A `
            -v ON_ERROR_STOP=1 -h ([string] $devRuntimeConnection['Host']) `
            -p ([string] $devRuntimeConnection['Port']) -U ([string] $devRuntimeConnection['Username']) `
            -d ([string] $devRuntimeConnection['Database']) | ForEach-Object { $_.Trim() } |
            Where-Object { $_ })
        if ($LASTEXITCODE -ne 0) {
            throw 'DEV_MIGRATION_REVIEW_INVALID: DEV migration history query failed.'
        }
        return $result
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Get-HttpStatus([string] $Url, [string] $Resolve = '') {
    $arguments = @('-sS', '--noproxy', '*', '--max-time', '12', '-o', 'NUL', '-w', '%{http_code}/%{ssl_verify_result}')
    if ($Resolve) { $arguments += @('--resolve', $Resolve) }
    $arguments += $Url
    $result = & $curlExe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'A DEV HTTPS smoke request failed.' }
    return [string] $result
}

function Assert-Smoke([string] $ExpectedSha) {
    if ((Get-HttpStatus 'https://localhost:7296/signin') -ne '302/0') { throw 'DEV localhost sign-in smoke failed.' }
    $publicResolve = 'csit-dev-da40.duckdns.org:7443:127.0.0.1'
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/' $publicResolve) -ne '302/0') {
        throw 'Public CSIT DEV HTTPS smoke failed.'
    }
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/app.css' $publicResolve) -ne '200/0' -or
        (Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/_framework/blazor.web.js' $publicResolve) -ne '200/0') {
        throw 'Public CSIT DEV static asset smoke failed.'
    }
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/swagger' $publicResolve) -ne '404/0') {
        throw 'Public DEV Swagger guard failed.'
    }
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7444/' 'csit-dev-da40.duckdns.org:7444:127.0.0.1') -ne '404/0') {
        throw 'The KM-only 7444 ingress guard failed.'
    }
    $negotiate = & $curlExe -sS --noproxy '*' --max-time 12 -X POST --resolve $publicResolve `
        'https://csit-dev-da40.duckdns.org:7443/_blazor/negotiate?negotiateVersion=1'
    if ($LASTEXITCODE -ne 0) { throw 'Blazor negotiate failed.' }
    $connection = $negotiate | ConvertFrom-Json
    if (-not $connection.connectionToken -or
        @($connection.availableTransports | Where-Object { $_.transport -eq 'WebSockets' }).Count -ne 1) {
        throw 'Blazor WebSocket negotiation failed.'
    }
    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $socket.Options.Proxy = $null
        $uri = [Uri] ('wss://csit-dev-da40.duckdns.org:7443/_blazor?id=' +
            [Uri]::EscapeDataString([string] $connection.connectionToken))
        $socket.ConnectAsync($uri, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($socket.State -ne [System.Net.WebSockets.WebSocketState]::Open) {
            throw 'Blazor WebSocket did not open.'
        }
    } finally {
        $socket.Abort()
        $socket.Dispose()
    }
    $devProcessId = Get-DevPid
    if ($devProcessId -eq 0) { throw 'DEV listener is absent after deployment.' }
    $process = Assert-DevProcess $devProcessId
    if ($process.ExecutablePath -notlike "*$ExpectedSha*") { throw 'Running DEV executable does not match the ready commit.' }
    $kmPids = @(Get-NetTCPConnection -State Listen -LocalPort 7444 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($kmPids.Count -ne 1 -or $kmPids[0] -ne $devProcessId) { throw 'DEV 7444 is not served by the deployed process.' }
    Write-Output "DEV_SMOKE_PASS $ExpectedSha PID=$devProcessId"
}

function Start-DevRelease([string] $ReleasePath, [string] $ReleaseSha) {
    Assert-Within $ReleasePath $releaseRoot
    $exe = Join-Path $ReleasePath 'SIT.DepartmentSystem.Web.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'The tested DEV executable is absent.' }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:CSIT_DEV_RUNTIME_SECRETS_FILE = $secretFile
    $env:CSIT_DEV_LOCAL_CERT_THUMBPRINT = $localhostCert
    $env:CSIT_DEV_7444_CERT_THUMBPRINT = $publicCert
    $env:DevDeployment__Sha = $ReleaseSha
    $env:DevDeployment__TimeUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss UTC')
    try {
        if ($env:ASPNETCORE_ENVIRONMENT -ne 'Development' -or $env:DOTNET_ENVIRONMENT -ne 'Development') {
            throw 'Development environment gate failed.'
        }
        $logPrefix = Join-Path $root ('logs\runtime-' + $ReleaseSha.Substring(0, 8) + '-' +
            (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))
        $process = Start-Process -FilePath $exe -WorkingDirectory $ReleasePath -WindowStyle Hidden `
            -RedirectStandardOutput ($logPrefix + '.stdout.log') `
            -RedirectStandardError ($logPrefix + '.stderr.log') -PassThru
        Start-Sleep -Seconds 5
        if ($process.HasExited) { throw 'The DEV runtime exited during startup.' }
        return $process.Id
    } finally {
        foreach ($key in @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'CSIT_DEV_RUNTIME_SECRETS_FILE',
                'CSIT_DEV_LOCAL_CERT_THUMBPRINT', 'CSIT_DEV_7444_CERT_THUMBPRINT',
                'DevDeployment__Sha', 'DevDeployment__TimeUtc')) {
            Remove-Item "Env:$key" -ErrorAction SilentlyContinue
        }
    }
}

function Stop-DevRelease([int] $ProcessId) {
    if ($ProcessId -eq 0) { return }
    $null = Assert-DevProcess $ProcessId
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $process.Kill()
    if (-not $process.WaitForExit(15000)) { throw 'DEV runtime did not stop.' }
}

if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-19') {
    throw 'The automatic DEV deployment must run as LOCAL SERVICE.'
}
if (-not $Tag.EndsWith($Sha.Substring(0, 8))) { throw 'The DEV ready tag does not match its commit.' }
Assert-Within $stateRoot $root
Assert-Within $releaseRoot $root
Assert-Within $workRoot $root
Assert-Within $reviewRoot $root
$productionBefore = Get-ProductionPid
Assert-ProductionUnchanged
Assert-DevRuntimeConfig

$lockPath = Join-Path $stateRoot 'deploy.lock'
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    throw 'A DEV deployment is already running.'
}

try {
    $tagLine = & $gitExe ls-remote --tags --refs $remote "refs/tags/$Tag"
    $branchLine = & $gitExe ls-remote $remote "refs/heads/$branch"
    if ($LASTEXITCODE -ne 0 -or $tagLine -notmatch "^$Sha\s+refs/tags/$Tag$" -or
        $branchLine -notmatch "^$Sha\s+refs/heads/$branch$") {
        throw 'The ready tag and designated DEV branch do not identify the same commit.'
    }

    $current = if (Test-Path -LiteralPath $currentFile) {
        Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
    } else { $null }
    $baselineFile = Join-Path $stateRoot 'baseline.json'
    $previousSha = if ($current) { [string] $current.Sha } else {
        [string] ((Get-Content -LiteralPath $baselineFile -Raw | ConvertFrom-Json).Sha)
    }
    if ($previousSha -notmatch '^[0-9a-f]{40}$') { throw 'The previous DEV commit is unknown.' }
    $releasePath = Join-Path $releaseRoot $Sha
    Assert-Within $releasePath $releaseRoot
    $work = Join-Path $workRoot $Sha
    Assert-Within $work $workRoot
    $releaseExists = Test-Path -LiteralPath (Join-Path $releasePath 'release.json')
    if (-not $releaseExists -or $previousSha -ne $Sha) {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        & $gitExe clone --quiet --branch $branch --single-branch --depth 100 $remote $work
        if ($LASTEXITCODE -ne 0 -or (& $gitExe -C $work rev-parse HEAD).Trim() -ne $Sha) {
            throw 'The isolated DEV checkout does not match the ready commit.'
        }
        if ($previousSha -ne $Sha) {
            & $gitExe -C $work cat-file -e "$previousSha^{commit}" 2>$null
            if ($LASTEXITCODE -ne 0) {
                & $gitExe -C $work fetch --quiet --unshallow origin
                if ($LASTEXITCODE -ne 0) { throw 'Cannot compare migration history.' }
            }
            $changed = @(& $gitExe -C $work diff --name-only $previousSha $Sha)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot compare release source changes.' }
            if (Test-DevMigrationChanges -ChangedPaths $changed) {
                $requiredMigrations = @(Get-RequiredDevMigrationIds -ChangedPaths $changed)
                if ($requiredMigrations.Count -eq 0) {
                    throw 'DEV_MIGRATION_REVIEW_REQUIRED: changed schema files have no reviewable Migration ID.'
                }
                $markerPath = Get-DevMigrationReviewMarkerPath -ReviewRoot $reviewRoot -Sha $Sha
                Assert-DevMigrationReviewMarkerAcl -MarkerPath $markerPath
                $appliedMigrations = @(Get-AppliedDevMigrations -MigrationIds $requiredMigrations)
                $null = Assert-DevMigrationReviewMarkerContent -MarkerPath $markerPath -Sha $Sha `
                    -Database 'sit_department_system_dev' -RequiredMigrationIds $requiredMigrations `
                    -AppliedMigrationIds $appliedMigrations
                Write-Output ('DEV_MIGRATION_REVIEW_VALID ' + $Sha + ' ' +
                    ($requiredMigrations -join ','))
            }
        }
    }
    if (-not $releaseExists) {
        Copy-Item -LiteralPath $baseSettings -Destination (Join-Path $work 'appsettings.json')
        $testProject = Join-Path $work 'tests\ApprovalFramework.Tests\ApprovalFramework.Tests.csproj'
        $webProject = Join-Path $work 'SIT.DepartmentSystem.Web.csproj'
        & $dotnetExe restore $testProject --nologo
        if ($LASTEXITCODE -ne 0) { throw 'DEV restore failed; the runtime was not touched.' }
        & $dotnetExe build $testProject -c Release --no-restore --nologo -p:SourceRevisionId=$Sha
        if ($LASTEXITCODE -ne 0) { throw 'DEV build failed; the runtime was not touched.' }
        & $dotnetExe test $testProject -c Release --no-build --no-restore --nologo
        if ($LASTEXITCODE -ne 0 -or $SimulateTestFailure) {
            throw 'DEV tests failed; the runtime was not touched.'
        }
        $publish = Join-Path $work 'dev-publish'
        & $dotnetExe publish $webProject -c Release --no-build --no-restore --nologo -o $publish `
            -p:SourceRevisionId=$Sha
        if ($LASTEXITCODE -ne 0) { throw 'DEV publish failed; the runtime was not touched.' }
        Copy-Item -LiteralPath (Join-Path $work 'appsettings.Development.json') -Destination $publish
        $builtDll = Join-Path $work 'bin\Release\net10.0\SIT.DepartmentSystem.Web.dll'
        $publishedDll = Join-Path $publish 'SIT.DepartmentSystem.Web.dll'
        $hash = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $publishedDll -Algorithm SHA256).Hash -ne $hash) {
            throw 'The published DLL differs from the tested build artifact.'
        }
        @{Sha=$Sha; Tag=$Tag; DllSha256=$hash; BuiltAtUtc=(Get-Date).ToUniversalTime().ToString('o')} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'release.json') -Encoding UTF8
        Move-Item -LiteralPath $publish -Destination $releasePath
        Write-Output "DEV_ARTIFACT_TESTED $Sha $hash"
    }

    $release = Get-Content -LiteralPath (Join-Path $releasePath 'release.json') -Raw | ConvertFrom-Json
    if ($release.Sha -ne $Sha -or
        (Get-FileHash -LiteralPath (Join-Path $releasePath 'SIT.DepartmentSystem.Web.dll') -Algorithm SHA256).Hash -ne $release.DllSha256) {
        throw 'The staged DEV release integrity check failed.'
    }
    if ($current -and $current.Sha -eq $Sha -and -not $SimulateSmokeFailure -and -not $SimulateTestFailure) {
        Assert-Smoke $Sha
        Write-Output "DEV_ALREADY_CURRENT $Sha"
        return
    }
    if ($SimulateTestFailure) { throw 'SIMULATED_DEV_TEST_FAILURE: runtime was not touched.' }
    $oldPid = Get-DevPid
    if (-not $current -and $oldPid -ne 0) {
        throw 'INITIAL_HANDOFF_REQUIRED: tested artifact is staged; current Administrator DEV must be stopped once.'
    }
    if ($oldPid -ne 0) {
        $null = Assert-DevProcess $oldPid
        if (-not $current -or -not $current.Path) {
            throw 'Current DEV process has no recoverable release manifest.'
        }
    }
    $oldRelease = if ($current) { [string] $current.Path } else { $null }
    if ($oldRelease) { Assert-Within $oldRelease $releaseRoot }
    if ($oldPid -ne 0) { Stop-DevRelease $oldPid }
    $candidatePid = 0
    try {
        $candidatePid = Start-DevRelease $releasePath $Sha
        Assert-Smoke $Sha
        if ($SimulateSmokeFailure) { throw 'SIMULATED_DEV_SMOKE_FAILURE' }
        Assert-ProductionUnchanged
        @{Sha=$Sha; Tag=$Tag; Path=$releasePath; Pid=$candidatePid;
            DllSha256=$release.DllSha256; DeployedAtUtc=(Get-Date).ToUniversalTime().ToString('o')} |
            ConvertTo-Json | Set-Content -LiteralPath ($currentFile + '.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($currentFile + '.tmp') -Destination $currentFile -Force
        Write-Output "DEV_DEPLOY_SUCCESS $Sha PID=$candidatePid"
    } catch {
        $failure = $_.Exception.Message
        if ($candidatePid -ne 0 -and (Get-Process -Id $candidatePid -ErrorAction SilentlyContinue)) {
            Stop-DevRelease $candidatePid
        }
        if ($oldRelease) {
            $rollbackPid = Start-DevRelease $oldRelease ([string] $current.Sha)
            Assert-Smoke ([string] $current.Sha)
            $current.Pid = $rollbackPid
            $current.DeployedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            $current | ConvertTo-Json | Set-Content -LiteralPath ($currentFile + '.tmp') -Encoding UTF8
            Move-Item -LiteralPath ($currentFile + '.tmp') -Destination $currentFile -Force
            Write-Output "DEV_ROLLBACK_PASS $($current.Sha) PID=$rollbackPid"
        }
        Assert-ProductionUnchanged
        throw "DEV deployment failed after activation: $failure"
    }
} finally {
    Assert-ProductionUnchanged
    if ($lock) { $lock.Dispose() }
}
