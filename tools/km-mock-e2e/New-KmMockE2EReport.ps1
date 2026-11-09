[CmdletBinding()]
param(
    [string] $ResultsRoot = 'artifacts/km-mock-e2e',
    [ValidateRange(1, 10000)][int] $MinimumExpectedTestsPerRound = 216,
    [switch] $Enforce
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $ResultsRoot | Out-Null

$coverageAreas = @(
    [ordered]@{ task='A'; area='Canonical v1/v2 contracts and idempotency'; classes=@('CanonicalKmContractTests','KmIntegrationTests','KmV2DocumentJobClientTests') },
    [ordered]@{ task='A'; area='Five sources: TestReport, DocumentLibrary, VerificationApplicationAttachment, VendorFile, EquipmentAttachment'; classes=@('TestReportKmEndToEndTests','DocumentLibraryKmIntegrationTests','KmV2PostgreSqlClaimTests') },
    [ordered]@{ task='B'; area='Document Library ingestion, download, callback and archive'; classes=@('DocumentLibraryKmIntegrationTests') },
    [ordered]@{ task='B'; area='Version replacement and immutable TestReport versions'; classes=@('TestReportKmEndToEndTests','EquipmentDocumentKmLifecycleTests') },
    [ordered]@{ task='C'; area='Analysis callback validation and transactionality'; classes=@('KmAnalysisIntegrationTests','KmTestReportCorrelationTests') },
    [ordered]@{ task='C'; area='Metadata Change lifecycle and revision persistence'; classes=@('KmMetadataChangeTests') },
    [ordered]@{ task='D'; area='Deletion v1/v2 contracts and callback receipts'; classes=@('KmDeletionContractTests') },
    [ordered]@{ task='D'; area='Vendor and Equipment deletion state machines'; classes=@('VendorAttachmentLifecycleTests','EquipmentFileKmDeletionTests') },
    [ordered]@{ task='D'; area='Deletion hold, tombstone and anti-resurrection'; classes=@('EquipmentSafeDeletionTests','DocumentLibraryFileDeletionTests') },
    [ordered]@{ task='A-D'; area='Lease takeover, retry, timeout and failure recovery'; classes=@('DocumentLibraryKmIntegrationTests','TestReportKmEndToEndTests','KmQueueMonitorTests') }
)

$rounds = @()
$allResults = @()
foreach ($round in 1..3) {
    $roundDirectory = Join-Path $ResultsRoot "round-$round"
    $statusPath = Join-Path $roundDirectory 'round-status.json'
    $status = if (Test-Path $statusPath) { Get-Content $statusPath -Raw | ConvertFrom-Json } else { $null }
    $trx = Get-ChildItem -Path $roundDirectory -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $results = @()
    if ($trx) {
        [xml]$xml = Get-Content $trx.FullName -Raw
        $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
            [pscustomobject]@{
                round = $round
                testName = [string]$_.testName
                outcome = [string]$_.outcome
                duration = [string]$_.duration
            }
        })
    }
    $allResults += $results
    $rounds += [ordered]@{
        round = $round
        status = if (-not $status -or $status.exitCode -ne 0) { 'FAIL' } elseif (-not $trx -or $results.Count -lt $MinimumExpectedTestsPerRound) { 'NOT RUN' } elseif (@($results | Where-Object outcome -eq 'Failed').Count -gt 0) { 'FAIL' } elseif (@($results | Where-Object outcome -ne 'Passed').Count -gt 0) { 'NOT RUN' } else { 'PASS' }
        passed = @($results | Where-Object outcome -eq 'Passed').Count
        failed = @($results | Where-Object outcome -eq 'Failed').Count
        notRun = @($results | Where-Object outcome -notin @('Passed','Failed')).Count
        trx = if ($trx) { $trx.FullName } else { $null }
    }
}

$matrix = @()
foreach ($area in $coverageAreas) {
    $row = [ordered]@{ task=$area.task; coverageArea=$area.area }
    foreach ($round in 1..3) {
        $matched = @($allResults | Where-Object {
            $result = $_
            if ($result.round -ne $round) { return $false }
            return @($area.classes | Where-Object { $result.testName -like "*$_*" }).Count -gt 0
        })
        $row["round$round"] = if ($matched.Count -eq 0) { 'NOT RUN' } elseif (@($matched | Where-Object outcome -eq 'Failed').Count -gt 0) { 'FAIL' } elseif (@($matched | Where-Object outcome -ne 'Passed').Count -gt 0) { 'NOT RUN' } else { 'PASS' }
    }
    $row.overall = if (@($row.round1,$row.round2,$row.round3) -contains 'FAIL') { 'FAIL' } elseif (@($row.round1,$row.round2,$row.round3) -contains 'NOT RUN') { 'NOT RUN' } else { 'PASS' }
    $matrix += [pscustomobject]$row
}

$lineRates = @()
foreach ($round in 1..3) {
    $file = Get-ChildItem -Path (Join-Path $ResultsRoot "round-$round") `
        -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { continue }
    $reader = [System.Xml.XmlReader]::Create($file.FullName)
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element -and
                $reader.Name -eq 'coverage') {
                $lineRates += [double]::Parse($reader.GetAttribute('line-rate'),
                    [Globalization.CultureInfo]::InvariantCulture)
                break
            }
        }
    }
    finally { $reader.Dispose() }
}
$averageLineRate = if ($lineRates.Count) { [Math]::Round((($lineRates | Measure-Object -Average).Average * 100), 2) } else { 0 }

$summary = [ordered]@{
    commit = $env:GITHUB_SHA
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    rounds = $rounds
    totals = [ordered]@{
        passed = @($allResults | Where-Object outcome -eq 'Passed').Count
        failed = @($allResults | Where-Object outcome -eq 'Failed').Count
        notRun = @($allResults | Where-Object outcome -notin @('Passed','Failed')).Count
    }
    codeCoverageAverageLinePercent = $averageLineRate
    coverageMatrix = $matrix
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ResultsRoot 'summary.json')
$matrix | Export-Csv -NoTypeInformation (Join-Path $ResultsRoot 'coverage-matrix.csv')
$allResults | Export-Csv -NoTypeInformation (Join-Path $ResultsRoot 'test-results.csv')

$markdown = @(
    '# CSIT x KM A-D Mock E2E acceptance',
    '',
    "Commit: ``$($env:GITHUB_SHA)``",
    '',
    "Tests: **$($summary.totals.passed) PASS / $($summary.totals.failed) FAIL / $($summary.totals.notRun) Not Run**",
    '',
    "Average line coverage across three rounds: **$averageLineRate%**",
    '',
    '## Round results',
    '',
    '| Round | Result | PASS | FAIL | Not Run |',
    '|---:|---|---:|---:|---:|'
)
foreach ($round in $rounds) { $markdown += "| $($round.round) | $($round.status) | $($round.passed) | $($round.failed) | $($round.notRun) |" }
$markdown += @('', '## Coverage matrix', '', '| Task | Coverage | Round 1 | Round 2 | Round 3 | Overall |', '|---|---|---|---|---|---|')
foreach ($row in $matrix) { $markdown += "| $($row.task) | $($row.coverageArea) | $($row.round1) | $($row.round2) | $($row.round3) | $($row.overall) |" }
$markdown += @('', 'All network peers are in-process Mock KM servers. Runtime KM dispatch flags remain false.', '')
$markdown -join "`n" | Set-Content (Join-Path $ResultsRoot 'summary.md')

if ($env:GITHUB_STEP_SUMMARY) { Get-Content (Join-Path $ResultsRoot 'summary.md') | Add-Content $env:GITHUB_STEP_SUMMARY }

$hasFailure = @($rounds | Where-Object status -ne 'PASS').Count -gt 0 -or
    @($matrix | Where-Object overall -ne 'PASS').Count -gt 0
if ($Enforce -and $hasFailure) { throw 'KM Mock E2E acceptance did not complete all three rounds successfully.' }
