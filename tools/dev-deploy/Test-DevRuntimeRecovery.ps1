$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Restart-DevCurrent.ps1') -LoadFunctionsOnly

function Assert-Decision([string] $Name, [string] $Expected, [hashtable] $Case) {
    $defaults = @{
        CurrentValid=$true; DevListenerCount=0; ReleaseProcessCount=0;
        RuntimeMatchesCurrent=$false; HealthPassed=$false; DependenciesReady=$true;
        NowUtc=[datetime]'2026-10-30T04:00:00Z'; LastAttemptUtc=$null;
        WindowStartedUtc=[datetime]'2026-10-30T04:00:00Z'; AttemptsInWindow=0
    }
    foreach ($key in $Case.Keys) { $defaults[$key] = $Case[$key] }
    $actual = Get-DevRuntimeRecoveryDecision @defaults
    if ($actual -ne $Expected) { throw "$Name expected $Expected but received $actual" }
    Write-Output "PASS $Name"
}

Assert-Decision 'missing runtime recovers current release' 'Recover' @{}
Assert-Decision 'healthy runtime is a no-op' 'Healthy' @{
    DevListenerCount=1; ReleaseProcessCount=1; RuntimeMatchesCurrent=$true; HealthPassed=$true
}
Assert-Decision 'database or storage unavailable blocks recovery' 'BlockedDependencies' @{
    DependenciesReady=$false
}
Assert-Decision 'unexpected existing runtime blocks a second process' 'BlockedExistingRuntime' @{
    DevListenerCount=1; ReleaseProcessCount=1; RuntimeMatchesCurrent=$false
}
Assert-Decision 'multiple listeners fail closed' 'BlockedDuplicate' @{
    DevListenerCount=2; ReleaseProcessCount=1
}
Assert-Decision 'recent attempt is rate limited' 'RetryDeferred' @{
    LastAttemptUtc=[Nullable[datetime]]([datetime]'2026-10-30T03:58:00Z')
}
Assert-Decision 'restart loop is capped' 'RetryLimit' @{
    AttemptsInWindow=3
}
Assert-Decision 'invalid current manifest fails closed' 'BlockedCurrent' @{
    CurrentValid=$false
}

function Assert-MaintenanceDecision([string] $Name, [string] $Expected, [hashtable] $Case) {
    $defaults = @{
        RequestValid=$true; CurrentValid=$true; DevListenerCount=1; ReleaseProcessCount=1;
        RuntimeMatchesCurrent=$true; HealthPassed=$true; DependenciesReady=$true
    }
    foreach ($key in $Case.Keys) { $defaults[$key] = $Case[$key] }
    $actual = Get-DevRuntimeMaintenanceDecision @defaults
    if ($actual -ne $Expected) { throw "$Name expected $Expected but received $actual" }
    Write-Output "PASS $Name"
}

Assert-MaintenanceDecision 'no maintenance request is a no-op' 'NoRequest' @{RequestValid=$false}
Assert-MaintenanceDecision 'maintenance requires exact runtime shape' 'BlockedRuntimeShape' @{DevListenerCount=0}
Assert-MaintenanceDecision 'maintenance refuses unhealthy runtime' 'BlockedUnhealthy' @{HealthPassed=$false}
Assert-MaintenanceDecision 'maintenance reloads a healthy exact release' 'Reload' @{}

function Assert-M2MDecision([string] $Name, [string] $Expected, [hashtable] $Case) {
    $defaults = @{
        RequestValid=$true; CurrentValid=$true; DevListenerCount=1; ReleaseProcessCount=1;
        RuntimeMatchesCurrent=$true; DependenciesReady=$true
    }
    foreach ($key in $Case.Keys) { $defaults[$key] = $Case[$key] }
    $actual = Get-DevM2MVerificationDecision @defaults
    if ($actual -ne $Expected) { throw "$Name expected $Expected but received $actual" }
    Write-Output "PASS $Name"
}

Assert-M2MDecision 'M2M verification accepts exact bound runtime' 'Verify' @{}
Assert-M2MDecision 'M2M verification rejects unbound runtime' 'BlockedUnboundRuntime' @{
    RuntimeMatchesCurrent=$false
}
Assert-M2MDecision 'M2M verification rejects duplicate runtime shape' 'BlockedRuntimeShape' @{
    DevListenerCount=2
}

$m2mFile = Join-Path ([IO.Path]::GetTempPath()) ('csit-m2m-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $current = [pscustomobject] @{ Sha = ('b' * 40); Pid = 5678;
        Path = 'C:\ProgramData\CSIT-DEV\releases\' + ('b' * 40) }
    $now = [datetime]::SpecifyKind([datetime]'2026-10-30T04:00:00', [DateTimeKind]::Utc)
    @{Action='VerifyKmM2MToken'; Sha=$current.Sha; Pid=$current.Pid; Path=$current.Path;
        ExpiresAtUtc=$now.AddMinutes(5).ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath $m2mFile -Encoding UTF8
    if (-not (Test-DevM2MVerificationRequest -RequestPath $m2mFile -Current $current -NowUtc $now)) {
        throw 'A matching unexpired M2M verification request was rejected.'
    }
    if (Test-DevM2MVerificationRequest -RequestPath $m2mFile -Current $current -NowUtc $now.AddMinutes(11)) {
        throw 'An expired M2M verification request was accepted.'
    }
    Write-Output 'PASS M2M verification request binding and expiry'
} finally {
    Remove-Item -LiteralPath $m2mFile -Force -ErrorAction SilentlyContinue
}

$maintenanceFile = Join-Path ([IO.Path]::GetTempPath()) ('csit-maintenance-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $current = [pscustomobject] @{ Action = 'ReloadRuntimeConfiguration'; Sha = ('a' * 40); Pid = 1234;
        Path = 'C:\ProgramData\CSIT-DEV\releases\' + ('a' * 40) }
    $now = [datetime]::SpecifyKind([datetime]'2026-10-30T04:00:00', [DateTimeKind]::Utc)
    @{Action='ReloadRuntimeConfiguration'; Sha=$current.Sha; Pid=$current.Pid; Path=$current.Path;
        ExpiresAtUtc=$now.AddMinutes(5).ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath $maintenanceFile -Encoding UTF8
    if (-not (Test-DevMaintenanceRequest -RequestPath $maintenanceFile -Current $current -NowUtc $now)) {
        throw 'A matching unexpired maintenance request was rejected.'
    }
    $expired = $now.AddMinutes(16)
    if (Test-DevMaintenanceRequest -RequestPath $maintenanceFile -Current $current -NowUtc $expired) {
        throw 'An expired maintenance request was accepted.'
    }
    Write-Output 'PASS maintenance request binding and expiry'
} finally {
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
}

$stateFile = Join-Path ([IO.Path]::GetTempPath()) ('csit-recovery-' + [guid]::NewGuid().ToString('N') + '.json')
$recoveryFile = $stateFile
try {
    $state = [pscustomobject] @{ WindowStartedUtc=[datetime]'2026-10-30T04:00:00Z';
        AttemptsInWindow=0; FailureCount=0 }
    Save-RecoveryState $state ([datetime]'2026-10-30T04:01:00Z') $false 'dependency' $false
    $blocked = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    if ($blocked.AttemptsInWindow -ne 0 -or $blocked.LastResult -ne 'Failed') {
        throw 'Dependency block state must not consume a start attempt.'
    }
    Save-RecoveryState $state ([datetime]'2026-10-30T04:06:00Z') $true 'started'
    $started = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    if ($started.AttemptsInWindow -ne 1 -or $started.LastResult -ne 'Succeeded') {
        throw 'Successful recovery state did not count one start attempt.'
    }
    Write-Output 'PASS dependency retry and start attempts are tracked separately'
} finally {
    Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ($stateFile + '.tmp') -Force -ErrorAction SilentlyContinue
}
