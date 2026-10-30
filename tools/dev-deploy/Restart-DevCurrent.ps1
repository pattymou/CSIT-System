param(
    [switch] $LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Get-DevRuntimeRecoveryDecision {
    param(
        [bool] $CurrentValid,
        [int] $DevListenerCount,
        [int] $ReleaseProcessCount,
        [bool] $RuntimeMatchesCurrent,
        [bool] $HealthPassed,
        [bool] $DependenciesReady,
        [datetime] $NowUtc,
        [AllowNull()] [object] $LastAttemptUtc,
        [datetime] $WindowStartedUtc,
        [int] $AttemptsInWindow,
        [int] $RetryMinutes = 5,
        [int] $MaximumAttemptsPerHour = 3
    )

    if (-not $CurrentValid) { return 'BlockedCurrent' }
    if ($DevListenerCount -gt 1 -or $ReleaseProcessCount -gt 1) { return 'BlockedDuplicate' }
    if ($DevListenerCount -eq 1 -or $ReleaseProcessCount -eq 1) {
        if ($DevListenerCount -eq 1 -and $ReleaseProcessCount -eq 1 -and
            $RuntimeMatchesCurrent -and $HealthPassed) {
            return 'Healthy'
        }
        return 'BlockedExistingRuntime'
    }
    if (-not $DependenciesReady) { return 'BlockedDependencies' }
    if ($null -ne $LastAttemptUtc -and
        ([datetime]$LastAttemptUtc).AddMinutes($RetryMinutes) -gt $NowUtc) {
        return 'RetryDeferred'
    }
    if ($WindowStartedUtc.AddHours(1) -gt $NowUtc -and
        $AttemptsInWindow -ge $MaximumAttemptsPerHour) {
        return 'RetryLimit'
    }
    return 'Recover'
}

$root = 'C:\ProgramData\CSIT-DEV'
$stateRoot = Join-Path $root 'state'
$releaseRoot = Join-Path $root 'releases'
$logsRoot = Join-Path $root 'logs'
$secretFile = Join-Path $root 'secrets\runtime.json'
$baseSettings = Join-Path $root 'secrets\base-appsettings.json'
$currentFile = Join-Path $stateRoot 'current.json'
$recoveryFile = Join-Path $stateRoot 'runtime-recovery.json'
$recoveryLog = Join-Path $logsRoot 'runtime-recovery.log'
$lock = $null
$candidatePid = 0
$curlExe = 'C:\Windows\System32\curl.exe'
$psqlExe = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$localhostCert = '46BFDA36B073576B3116CB5E52D7ABE1FC595F2D'
$publicCert = 'D4E5BA5E6D6252575BB8D0ADD3F2A98B279C503B'

function Write-RecoveryLog([string] $Level, [string] $Message) {
    $line = '{0} {1} {2}' -f (Get-Date).ToUniversalTime().ToString('o'), $Level, $Message
    Add-Content -LiteralPath $recoveryLog -Value $line -Encoding UTF8
}

function Assert-Within([string] $Path, [string] $Parent) {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($parentPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The path is outside the DEV deployment root.'
    }
    if ($Path -match '^[dD]:\\CSIT_Publish') { throw 'Production publish path is forbidden.' }
}

function Test-FullyQualifiedStoragePath([string] $Path) {
    return $Path -match '^[A-Za-z]:[\\/]' -or $Path -match '^\\\\[^\\]+\\[^\\]+'
}

function Get-ProductionPid {
    $service = Get-CimInstance Win32_Service -Filter "Name='SIT.DepartmentSystem.Web'"
    if ($null -eq $service -or $service.State -ne 'Running') {
        throw 'Production service is not running; DEV recovery is blocked.'
    }
    $pids = @(Get-NetTCPConnection -State Listen -LocalPort 5010 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($pids.Count -ne 1) { throw 'Production 5010 PID guard failed.' }
    return [int] $pids[0]
}

function Get-DevListeners {
    return @(Get-NetTCPConnection -State Listen -LocalPort 7296 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Get-DevReleaseProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name='SIT.DepartmentSystem.Web.exe'" |
        Where-Object { $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\',
                [StringComparison]::OrdinalIgnoreCase) })
}

function Get-HttpStatus([string] $Url, [string] $Resolve = '') {
    $arguments = @('-sS', '--noproxy', '*', '--max-time', '12', '-o', 'NUL',
        '-w', '%{http_code}/%{ssl_verify_result}')
    if ($Resolve) { $arguments += @('--resolve', $Resolve) }
    $arguments += $Url
    $result = & $curlExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "HTTPS request failed: $Url" }
    return [string] $result
}

function Get-OidcRedirectLocation {
    $headers = @(& $curlExe -sS --noproxy '*' --max-time 12 -D - -o NUL `
        'https://localhost:7296/signin')
    if ($LASTEXITCODE -ne 0) { throw 'DEV OIDC redirect request failed.' }
    $status = @($headers | Where-Object { $_ -match '^HTTP/' } | Select-Object -Last 1)
    $location = @($headers | Where-Object { $_ -match '^(?i)Location:' } |
        Select-Object -Last 1)
    if ($status -notmatch '\s302\s' -or $location -notmatch
        '^Location:\s*https://csit-identity-dev\.duckdns\.org:7443/connect/authorize') {
        throw 'DEV OIDC redirect target is invalid.'
    }
}

function Assert-Smoke([string] $ExpectedSha, [int] $ExpectedPid) {
    if ((Get-HttpStatus 'https://localhost:7296/signin') -ne '302/0') {
        throw 'DEV localhost sign-in smoke failed.'
    }
    Get-OidcRedirectLocation
    $publicResolve = 'csit-dev-da40.duckdns.org:7443:127.0.0.1'
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/' $publicResolve) -ne '302/0' -or
        (Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/app.css' $publicResolve) -ne '200/0' -or
        (Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7443/_framework/blazor.web.js' $publicResolve) -ne '200/0') {
        throw 'Public CSIT DEV HTTPS smoke failed.'
    }
    if ((Get-HttpStatus 'https://csit-dev-da40.duckdns.org:7444/' `
            'csit-dev-da40.duckdns.org:7444:127.0.0.1') -ne '404/0') {
        throw 'The KM-only 7444 ingress guard failed.'
    }
    $listeners = Get-DevListeners
    if ($listeners.Count -ne 1 -or [int]$listeners[0] -ne $ExpectedPid) {
        throw 'DEV 7296 listener PID changed.'
    }
    $kmPids = @(Get-NetTCPConnection -State Listen -LocalPort 7444 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($kmPids.Count -ne 1 -or [int]$kmPids[0] -ne $ExpectedPid) {
        throw 'DEV 7444 is not served by the expected process.'
    }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedPid"
    if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
        $process.ExecutablePath -notlike "*$ExpectedSha*") {
        throw 'Running DEV executable does not match current.json.'
    }
}

function Get-RecoveryState([datetime] $NowUtc) {
    if (-not (Test-Path -LiteralPath $recoveryFile)) {
        return [pscustomobject] @{ LastAttemptUtc=$null; WindowStartedUtc=$NowUtc;
            AttemptsInWindow=0; FailureCount=0 }
    }
    try {
        $state = Get-Content -LiteralPath $recoveryFile -Raw | ConvertFrom-Json
        $window = [datetime]::Parse([string]$state.WindowStartedUtc).ToUniversalTime()
        if ($window.AddHours(1) -le $NowUtc) {
            return [pscustomobject] @{ LastAttemptUtc=$null; WindowStartedUtc=$NowUtc;
                AttemptsInWindow=0; FailureCount=0 }
        }
        $last = if ($state.LastAttemptUtc) {
            [Nullable[datetime]]([datetime]::Parse([string]$state.LastAttemptUtc).ToUniversalTime())
        } else { $null }
        return [pscustomobject] @{ LastAttemptUtc=$last; WindowStartedUtc=$window;
            AttemptsInWindow=[int]$state.AttemptsInWindow; FailureCount=[int]$state.FailureCount }
    } catch {
        throw 'DEV runtime recovery state is malformed.'
    }
}

function Save-RecoveryState($State, [datetime] $NowUtc, [bool] $Succeeded, [string] $Reason,
    [bool] $CountAttempt = $true) {
    @{LastAttemptUtc=$NowUtc.ToString('o'); WindowStartedUtc=$State.WindowStartedUtc.ToString('o');
        AttemptsInWindow=([int]$State.AttemptsInWindow + $(if($CountAttempt){1}else{0}));
        FailureCount=if($Succeeded){0}else{([int]$State.FailureCount + 1)};
        LastResult=if($Succeeded){'Succeeded'}else{'Failed'}; Reason=$Reason} |
        ConvertTo-Json | Set-Content -LiteralPath ($recoveryFile + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($recoveryFile + '.tmp') -Destination $recoveryFile -Force
}

function Assert-DevDependencies {
    if (-not (Test-Path -LiteralPath $secretFile) -or
        -not (Test-Path -LiteralPath $baseSettings)) {
        throw 'Restricted DEV runtime configuration is incomplete.'
    }
    $secrets = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    $connection.set_ConnectionString(
        [string]$secrets.PSObject.Properties['ConnectionStrings:Postgres'].Value)
    if ([string]$connection['Database'] -ne 'sit_department_system_dev' -or
        [string]$connection['Username'] -ne 'csit_runtime_dev' -or
        -not $connection.ContainsKey('Password')) {
        throw 'The runtime database is not the authorized DEV role and database.'
    }
    $env:PGPASSWORD = [string]$connection['Password']
    try {
        $query = @'
SELECT current_database() || '|' || current_user;
SELECT value FROM system_options WHERE is_enabled = TRUE AND category IN
('ApprovalStagingRoot','AssetRootPath','ThreeLevelRootPath') ORDER BY category, name;
SELECT (SELECT count(*) FROM equipment_deletion_jobs WHERE status <> 'Completed') || '|' ||
       (SELECT count(*) FROM equipment_file_deletion_jobs WHERE status <> 'Completed');
'@
        $result = @($query | & $psqlExe -X -w -t -A -v ON_ERROR_STOP=1 `
            -h ([string]$connection['Host']) -p ([string]$connection['Port']) `
            -U ([string]$connection['Username']) -d ([string]$connection['Database']) |
            ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($LASTEXITCODE -ne 0 -or $result.Count -lt 3 -or
            $result[0] -ne 'sit_department_system_dev|csit_runtime_dev') {
            throw 'The DEV runtime database connectivity guard failed.'
        }
        if ($result[-1] -ne '0|0') {
            throw 'Active equipment deletion work blocks automatic DEV recovery.'
        }
        $paths = @($result[1..($result.Count - 2)])
        foreach ($key in @('RawData:NasRootPath','DocumentStorage:RootPath','DocumentStaging:RootPath')) {
            $property = $secrets.PSObject.Properties[$key]
            if ($property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                $paths += [string]$property.Value
            }
        }
        foreach ($path in @($paths | Sort-Object -Unique)) {
            if (-not (Test-FullyQualifiedStoragePath $path) -or
                -not (Test-Path -LiteralPath $path -PathType Container)) {
                throw "A required DEV storage root is unavailable: $path"
            }
        }
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    $identity = Get-HttpStatus 'https://csit-identity-dev.duckdns.org:7443/account/login' `
        'csit-identity-dev.duckdns.org:7443:127.0.0.1'
    if ($identity -notin @('200/0','302/0')) {
        throw 'Shared Identity DEV is unavailable.'
    }
}

function Start-CurrentRelease([string] $ReleasePath, [string] $ReleaseSha) {
    $exe = Join-Path $ReleasePath 'SIT.DepartmentSystem.Web.exe'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:CSIT_DEV_RUNTIME_SECRETS_FILE = $secretFile
    $env:CSIT_DEV_LOCAL_CERT_THUMBPRINT = $localhostCert
    $env:CSIT_DEV_7444_CERT_THUMBPRINT = $publicCert
    $env:DevDeployment__Sha = $ReleaseSha
    $env:DevDeployment__TimeUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss UTC')
    try {
        $logPrefix = Join-Path $logsRoot ('runtime-' + $ReleaseSha.Substring(0, 8) +
            '-recovery-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))
        $process = Start-Process -FilePath $exe -WorkingDirectory $ReleasePath `
            -WindowStyle Hidden -RedirectStandardOutput ($logPrefix + '.stdout.log') `
            -RedirectStandardError ($logPrefix + '.stderr.log') -PassThru
        Start-Sleep -Seconds 5
        if ($process.HasExited) { throw 'The DEV runtime exited during recovery startup.' }
        return $process.Id
    } finally {
        foreach ($key in @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT',
                'CSIT_DEV_RUNTIME_SECRETS_FILE','CSIT_DEV_LOCAL_CERT_THUMBPRINT',
                'CSIT_DEV_7444_CERT_THUMBPRINT','DevDeployment__Sha','DevDeployment__TimeUtc')) {
            Remove-Item "Env:$key" -ErrorAction SilentlyContinue
        }
    }
}

if ($LoadFunctionsOnly) { return }

if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-19') {
    throw 'DEV runtime recovery must run as LOCAL SERVICE.'
}

try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'deploy.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    return
}

try {
    if (-not (Test-Path -LiteralPath $currentFile)) { return }
    $current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
    $currentValid = $current.Sha -match '^[0-9a-f]{40}$' -and $current.Path
    if ($currentValid) {
        $expectedPath = Join-Path $releaseRoot ([string]$current.Sha)
        $currentValid = [IO.Path]::GetFullPath([string]$current.Path).TrimEnd('\') -eq
            [IO.Path]::GetFullPath($expectedPath).TrimEnd('\')
    }
    if ($currentValid) {
        Assert-Within ([string]$current.Path) $releaseRoot
        $manifestPath = Join-Path ([string]$current.Path) 'release.json'
        $dllPath = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.dll'
        $exePath = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'
        if (-not (Test-Path -LiteralPath $manifestPath) -or
            -not (Test-Path -LiteralPath $dllPath) -or -not (Test-Path -LiteralPath $exePath)) {
            $currentValid = $false
        } else {
            $release = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $currentValid = $release.Sha -eq $current.Sha -and
                (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash -eq $release.DllSha256
        }
    }

    $listeners = Get-DevListeners
    $processes = Get-DevReleaseProcesses
    $runtimeMatches = $false
    $healthPassed = $false
    if ($currentValid -and $listeners.Count -eq 1 -and $processes.Count -eq 1) {
        $runtimeMatches = [int]$listeners[0] -eq [int]$processes[0].ProcessId -and
            [int]$current.Pid -eq [int]$processes[0].ProcessId -and
            [IO.Path]::GetFullPath([string]$processes[0].ExecutablePath) -eq
                [IO.Path]::GetFullPath((Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'))
        if ($runtimeMatches) {
            try {
                Assert-Smoke ([string]$current.Sha) ([int]$processes[0].ProcessId)
                $healthPassed = $true
            } catch { $healthPassed = $false }
        }
    }
    $now = (Get-Date).ToUniversalTime()
    $state = Get-RecoveryState $now
    $preDecision = Get-DevRuntimeRecoveryDecision -CurrentValid $currentValid `
        -DevListenerCount $listeners.Count -ReleaseProcessCount $processes.Count `
        -RuntimeMatchesCurrent $runtimeMatches -HealthPassed $healthPassed `
        -DependenciesReady $true -NowUtc $now -LastAttemptUtc $state.LastAttemptUtc `
        -WindowStartedUtc $state.WindowStartedUtc -AttemptsInWindow $state.AttemptsInWindow
    if ($preDecision -eq 'Healthy') { return }
    if ($preDecision -ne 'Recover') {
        if ($preDecision -notin @('RetryDeferred','RetryLimit')) {
            Write-RecoveryLog 'BLOCKED' $preDecision
        }
        return
    }

    try {
        $productionBefore = Get-ProductionPid
        Assert-DevDependencies
    } catch {
        Save-RecoveryState $state $now $false ([string]$_.Exception.Message) $false
        Write-RecoveryLog 'BLOCKED' ([string]$_.Exception.Message)
        return
    }
    if ((Get-DevListeners).Count -ne 0 -or (Get-DevReleaseProcesses).Count -ne 0) {
        Write-RecoveryLog 'BLOCKED' 'A DEV process appeared during dependency checks.'
        return
    }

    try {
        $candidatePid = Start-CurrentRelease ([string]$current.Path) ([string]$current.Sha)
        Assert-Smoke ([string]$current.Sha) $candidatePid
        if ((Get-ProductionPid) -ne $productionBefore) {
            throw 'Production 5010 PID changed during DEV recovery.'
        }
        $current.Pid = $candidatePid
        $current | Add-Member -NotePropertyName RecoveredAtUtc -NotePropertyValue $now.ToString('o') -Force
        $current | ConvertTo-Json | Set-Content -LiteralPath ($currentFile + '.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($currentFile + '.tmp') -Destination $currentFile -Force
        Save-RecoveryState $state $now $true 'DEV_RUNTIME_RECOVERY_SUCCESS'
        Write-RecoveryLog 'SUCCESS' ("sha=$($current.Sha) pid=$candidatePid")
    } catch {
        $reason = [string]$_.Exception.Message
        if ($candidatePid -ne 0) {
            $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$candidatePid"
            $expectedExe = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'
            if ($candidate -and $candidate.Name -eq 'SIT.DepartmentSystem.Web.exe' -and
                [IO.Path]::GetFullPath([string]$candidate.ExecutablePath) -eq
                    [IO.Path]::GetFullPath($expectedExe)) {
                Stop-Process -Id $candidatePid -Force
            }
        }
        Save-RecoveryState $state $now $false $reason
        Write-RecoveryLog 'FAILED' $reason
    }
} catch {
    Write-RecoveryLog 'FAILED' ([string]$_.Exception.Message)
} finally {
    if ($lock) { $lock.Dispose() }
}
