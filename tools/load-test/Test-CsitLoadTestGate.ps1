[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "csit-load-gate-$([Guid]::NewGuid().ToString('N'))"
$report = Join-Path $PSScriptRoot 'New-CsitLoadTestReport.ps1'
$operations = @('read','edit','verification_attachment','verification_submit','verification_approval',
    'bypass_admin','bypass_teamleader','upload_equipment','upload_equipment_pdf',
    'upload_equipment_png','upload_equipment_jpg','upload_vendor','upload_document','approval',
    'reservation','reservation_approval','download','delete')
function Write-Evidence([string] $Missing = '', [int] $ConnectionExhaustionEvents = 0) {
    $directory = Join-Path $root 'level-5'; New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $metrics = [ordered]@{
        checks = @{ values=@{ rate=1 } }
        system_failure = @{ values=@{ rate=0 } }
        authorization_violation = @{ values=@{ rate=0 } }
        correctness_violation = @{ values=@{ rate=0 } }
    }
    foreach ($operation in $operations) {
        if ($operation -eq $Missing) { continue }
        $metrics["op_${operation}_success"] = @{ values=@{ rate=1 } }
        $metrics["op_${operation}_duration"] = @{ values=@{ count=1; med=1; 'p(95)'=2; 'p(99)'=3 } }
    }
    @{ metrics=$metrics } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'k6-summary.json')
    @{ passed=$true } | ConvertTo-Json | Set-Content (Join-Path $directory 'invariants.json')
    @{ connections=1; total_connections=2; max_connections=100; superuser_reserved_connections=3;
        applicationPoolMaximum=80; peakConnections=50; peakDatabaseConnections=49;
        connectionSamples=10; connectionSampleFailures=0;
        connectionExhaustionEvents=$ConnectionExhaustionEvents } |
        ConvertTo-Json | Set-Content (Join-Path $directory 'postgres-metrics.json')
}
try {
    Write-Evidence
    & $report -ResultsRoot $root -ExpectedLevels 5 -Workload mixed -Enforce
    Write-Evidence -Missing 'reservation'
    $failedClosed = $false
    try { & $report -ResultsRoot $root -ExpectedLevels 5 -Workload mixed -Enforce }
    catch { $failedClosed = $true }
    if (!$failedClosed) { throw 'Regression: a Not Run load operation was accepted.' }
    Write-Evidence -ConnectionExhaustionEvents 1
    $failedClosed = $false
    try { & $report -ResultsRoot $root -ExpectedLevels 5 -Workload mixed -Enforce }
    catch { $failedClosed = $true }
    if (!$failedClosed) { throw 'Regression: PostgreSQL connection exhaustion was accepted.' }
    Write-Output 'Load-test Gate self-test passed: Not Run operations and connection exhaustion fail closed.'
}
finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
