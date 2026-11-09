[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$report = Join-Path $PSScriptRoot 'New-KmMockE2EReport.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "csit-km-gate-$([Guid]::NewGuid().ToString('N'))"
$classes = @(
    'CanonicalKmContractTests', 'KmIntegrationTests', 'KmV2DocumentJobClientTests',
    'KmV2PostgreSqlClaimTests', 'TestReportKmEndToEndTests',
    'DocumentLibraryKmIntegrationTests', 'KmAnalysisIntegrationTests',
    'KmMetadataChangeTests', 'KmTestReportCorrelationTests',
    'EquipmentDocumentKmLifecycleTests', 'VendorAttachmentLifecycleTests',
    'EquipmentFileKmDeletionTests', 'EquipmentSafeDeletionTests',
    'DocumentLibraryFileDeletionTests', 'KmDeletionContractTests', 'KmQueueMonitorTests'
)

function Write-SyntheticRound {
    param([int] $Round, [string] $ExceptionalOutcome = 'Passed')
    $directory = Join-Path $testRoot "round-$Round"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    @{ round=$Round; exitCode=0 } | ConvertTo-Json | Set-Content (Join-Path $directory 'round-status.json')
    $results = for ($index = 0; $index -lt $classes.Count; $index++) {
        $outcome = if ($index -eq 0) { $ExceptionalOutcome } else { 'Passed' }
        "<UnitTestResult testName='$($classes[$index]).Synthetic' outcome='$outcome' duration='00:00:00.001' />"
    }
    "<TestRun><Results>$($results -join '')</Results></TestRun>" |
        Set-Content (Join-Path $directory "round-$Round.trx")
}

try {
    1..3 | ForEach-Object { Write-SyntheticRound -Round $_ }
    & $report -ResultsRoot $testRoot -MinimumExpectedTestsPerRound 1 -Enforce

    Write-SyntheticRound -Round 2 -ExceptionalOutcome 'NotExecuted'
    $failedClosed = $false
    try { & $report -ResultsRoot $testRoot -MinimumExpectedTestsPerRound 1 -Enforce }
    catch { $failedClosed = $true }
    if (-not $failedClosed) { throw 'Regression: a NotExecuted test was incorrectly accepted as PASS.' }

    Write-SyntheticRound -Round 2 -ExceptionalOutcome 'Skipped'
    $failedClosed = $false
    try { & $report -ResultsRoot $testRoot -MinimumExpectedTestsPerRound 1 -Enforce }
    catch { $failedClosed = $true }
    if (-not $failedClosed) { throw 'Regression: a Skipped test was incorrectly accepted as PASS.' }

    Write-Output 'Gate self-test passed: NotExecuted and Skipped both fail acceptance.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
