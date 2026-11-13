$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevNasHierarchyEvidence.ps1')

$preMigrationSha = '1111111111111111111111111111111111111111'
$applyCommitSha = '2222222222222222222222222222222222222222'
$unrelatedSha = '3333333333333333333333333333333333333333'
$root = Join-Path ([IO.Path]::GetTempPath()) ('csit-nas-evidence-' + [guid]::NewGuid().ToString('N'))
$passed = 0

function Assert-Throws([scriptblock] $Action, [string] $Name) {
    try { & $Action; throw "Expected rejection was not raised: $Name" }
    catch {
        if ($_.Exception.Message -like 'Expected rejection was not raised:*') { throw }
        $script:passed++
        Write-Output "PASS $Name"
    }
}

function New-TestEvidence([string] $Path) {
    New-Item -ItemType Directory -Path $Path | Out-Null
    [ordered]@{
        PreMigrationRuntimeSha = $preMigrationSha
        ApplyCommitSha = $applyCommitSha
        NasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'
        TechnicalRoot = '\\AI-NAS\Workspace_B\CSIT_TECH_DATA_DEV'
        OriginalsRetained = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Path 'summary.json') -Encoding UTF8
    foreach ($name in @('file-manifest.csv','database-switch.sql','database-rollback.sql',
            'runtime.before.json','current.before.json')) {
        "fixture:$name" | Set-Content -LiteralPath (Join-Path $Path $name) -Encoding UTF8
    }
    $null = New-DevNasHierarchyEvidenceIntegrity $Path $preMigrationSha $applyCommitSha
}

try {
    $valid = Join-Path $root 'valid'
    New-TestEvidence $valid
    $evidence = Read-DevNasHierarchyEvidence $valid
    if ((Get-DevNasHierarchyRollbackMode $evidence $applyCommitSha $preMigrationSha) -ne
        'BeforeDeployment') { throw 'Before-deployment rollback mode was not selected.' }
    $passed++; Write-Output 'PASS apply_after_before_deployment'
    if ((Get-DevNasHierarchyRollbackMode $evidence $applyCommitSha $applyCommitSha) -ne
        'AfterDeployment') { throw 'After-deployment rollback mode was not selected.' }
    $passed++; Write-Output 'PASS apply_after_new_deployment'
    Assert-Throws { Get-DevNasHierarchyRollbackMode $evidence $applyCommitSha $unrelatedSha } `
        'unrelated_runtime_sha_rejected'
    Assert-Throws { Get-DevNasHierarchyRollbackMode $evidence $unrelatedSha $preMigrationSha } `
        'unrelated_apply_sha_rejected'

    $tampered = Join-Path $root 'tampered'
    New-TestEvidence $tampered
    Add-Content -LiteralPath (Join-Path $tampered 'summary.json') -Value 'tampered'
    Assert-Throws { Read-DevNasHierarchyEvidence $tampered } 'modified_evidence_rejected'

    $incomplete = Join-Path $root 'incomplete'
    New-TestEvidence $incomplete
    Remove-Item -LiteralPath (Join-Path $incomplete 'database-rollback.sql')
    Assert-Throws { Read-DevNasHierarchyEvidence $incomplete } 'incomplete_evidence_rejected'

    if ($passed -ne 6) { throw "Unexpected contract test count: $passed" }
    Write-Output "DEV_NAS_HIERARCHY_EVIDENCE_TEST_PASS count=$passed"
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
