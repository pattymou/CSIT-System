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
