[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsRoot,
    [Parameter(Mandatory)][int[]] $ExpectedLevels,
    [Parameter(Mandatory)][string] $Workload,
    [switch] $Enforce
)
$ErrorActionPreference = 'Stop'
$requiredByWorkload = @{
    mixed = @('read','edit','verification_attachment','verification_submit','verification_approval',
        'bypass_admin','bypass_teamleader','upload_equipment','upload_equipment_pdf',
        'upload_equipment_png','upload_equipment_jpg','upload_vendor','upload_document','approval',
        'reservation','reservation_approval','download','delete')
    documents = @('read','edit','bypass_admin','bypass_teamleader','upload_equipment',
        'upload_equipment_pdf','upload_equipment_png','upload_equipment_jpg','upload_vendor',
        'upload_document','approval','download','delete')
    reservations = @('read','edit','reservation','reservation_approval')
    concurrency = @('read','edit','verification_attachment','verification_submit',
        'verification_approval','reservation','reservation_approval')
}
$rows = @(); $levelRows = @(); $databaseInvariants = @(); $postgresMetrics = @()
$failures = [Collections.Generic.List[string]]::new()
foreach ($level in $ExpectedLevels) {
    $directory = Join-Path $ResultsRoot "level-$level"
    $summaryPath = Join-Path $directory 'k6-summary.json'
    $invariantPath = Join-Path $directory 'invariants.json'
    $postgresPath = Join-Path $directory 'postgres-metrics.json'
    if (!(Test-Path -LiteralPath $summaryPath) -or !(Test-Path -LiteralPath $invariantPath) -or
        !(Test-Path -LiteralPath $postgresPath)) {
        $failures.Add("Level $level did not produce complete evidence."); continue
    }
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $invariant = Get-Content -LiteralPath $invariantPath -Raw | ConvertFrom-Json
    $postgres = Get-Content -LiteralPath $postgresPath -Raw | ConvertFrom-Json
    $levelRows += [pscustomobject]@{
        Level = $level
        Rps = [double]$summary.metrics.http_reqs.values.rate
        HttpErrorRate = [double]$summary.metrics.http_req_failed.values.rate
        CheckSuccessRate = [double]$summary.metrics.checks.values.rate
        Iterations = [int]$summary.metrics.iterations.values.count
        DatabasePassed = $invariant.passed -eq $true
    }
    $databaseInvariants += [pscustomobject]@{ Level=$level; Invariants=$invariant }
    $postgresMetrics += [pscustomobject]@{ Level=$level; Metrics=$postgres }
    if ([int]$postgres.max_connections -lt 1 -or [int]$postgres.applicationPoolMaximum -lt 1 -or
        [int]$postgres.peakConnections -lt 1) {
        $failures.Add("Level $level has incomplete PostgreSQL connection capacity evidence.")
    }
    if ([int]$postgres.connectionSamples -lt 1) { $failures.Add("Level $level did not sample PostgreSQL connections.") }
    if ([int]$postgres.connectionSampleFailures -gt 0) {
        $failures.Add("Level $level had $($postgres.connectionSampleFailures) PostgreSQL connection sample failures.")
    }
    if ([int]$postgres.connectionExhaustionEvents -gt 0) {
        $failures.Add("Level $level exhausted PostgreSQL connections $($postgres.connectionExhaustionEvents) time(s).")
    }
    foreach ($metric in @('checks','system_failure','authorization_violation','correctness_violation')) {
        if ($null -eq $summary.metrics.$metric) { $failures.Add("Level $level metric $metric was Not Run.") }
    }
    if ($summary.metrics.checks.values.rate -ne 1) { $failures.Add("Level $level has failed checks.") }
    foreach ($metric in @('system_failure','authorization_violation','correctness_violation')) {
        if ($summary.metrics.$metric.values.rate -ne 0) { $failures.Add("Level $level has nonzero $metric.") }
    }
    if ($invariant.passed -ne $true) { $failures.Add("Level $level failed data invariants.") }
    foreach ($operation in $requiredByWorkload[$Workload]) {
        $successMetric = $summary.metrics."op_${operation}_success"
        $durationMetric = $summary.metrics."op_${operation}_duration"
        if ($null -eq $successMetric -or $null -eq $durationMetric -or $durationMetric.values.count -lt 1) {
            $failures.Add("Level $level operation $operation was Not Run."); continue
        }
        if ($successMetric.values.rate -ne 1) { $failures.Add("Level $level operation $operation has failed requests.") }
        $rows += [pscustomobject]@{ Level=$level; Operation=$operation; Requests=[int]$durationMetric.values.count;
            SuccessRate=[double]$successMetric.values.rate; P50=[double]$durationMetric.values.'med';
            P95=[double]$durationMetric.values.'p(95)'; P99=[double]$durationMetric.values.'p(99)' }
    }
}

$report = [ordered]@{ generatedAtUtc=[DateTime]::UtcNow.ToString('O'); workload=$Workload;
    levels=$ExpectedLevels; passed=($failures.Count -eq 0); failures=$failures;
    summaries=$levelRows; databaseInvariants=$databaseInvariants; postgresMetrics=$postgresMetrics; operations=$rows }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ResultsRoot 'report.json') -Encoding utf8
$lines = @('# CSIT Multi-User Load Test', '', "- Workload: **$Workload**",
    "- Levels: **$($ExpectedLevels -join ', ') users**", "- Gate: **$(if ($failures.Count) {'FAIL'} else {'PASS'})**", '',
    '| Users | Iterations | RPS | HTTP error rate | Check success | DB invariants |',
    '|---:|---:|---:|---:|---:|---:|')
foreach ($levelRow in $levelRows) { $lines += "| $($levelRow.Level) | $($levelRow.Iterations) | $([Math]::Round($levelRow.Rps,2)) | $([Math]::Round(100*$levelRow.HttpErrorRate,2))% | $([Math]::Round(100*$levelRow.CheckSuccessRate,2))% | $(if ($levelRow.DatabasePassed) {'PASS'} else {'FAIL'}) |" }
$lines += @('', '| Users | PG peak | PG limit | App pool limit | Exhaustion events | Sample failures |',
    '|---:|---:|---:|---:|---:|---:|')
foreach ($row in $postgresMetrics) {
    $metric = $row.Metrics
    $lines += "| $($row.Level) | $($metric.peakConnections) | $($metric.max_connections) | $($metric.applicationPoolMaximum) | $($metric.connectionExhaustionEvents) | $($metric.connectionSampleFailures) |"
}
$lines += @('',
    '| Users | Operation | Requests | Success | P50 ms | P95 ms | P99 ms |',
    '|---:|---|---:|---:|---:|---:|---:|')
foreach ($row in $rows) { $lines += "| $($row.Level) | $($row.Operation) | $($row.Requests) | $([Math]::Round(100*$row.SuccessRate,2))% | $([Math]::Round($row.P50,2)) | $([Math]::Round($row.P95,2)) | $([Math]::Round($row.P99,2)) |" }
$lines += @('', 'Latency is a traceable baseline, not a capacity certification. Correctness and safety are the only hard gates.')
if ($failures.Count) { $lines += @('', '## Failures', '') + @($failures | ForEach-Object { "- $_" }) }
$lines | Set-Content -LiteralPath (Join-Path $ResultsRoot 'report.md') -Encoding utf8
if ($Enforce -and $failures.Count) { throw "CSIT load-test Gate failed: $($failures -join ' ')" }
Write-Output "CSIT load-test Gate $(if ($failures.Count) {'FAIL'} else {'PASS'}): $($rows.Count) operation rows."
