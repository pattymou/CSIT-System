$script:DevNasHierarchyEvidenceSchemaVersion = 2
$script:DevNasHierarchyEvidenceFiles = @(
    'summary.json',
    'file-manifest.csv',
    'database-switch.sql',
    'database-rollback.sql',
    'runtime.before.json',
    'current.before.json'
)

function Assert-DevNasHierarchySha([string] $Sha, [string] $Role) {
    if ($Sha -notmatch '^[0-9a-f]{40}$') {
        throw "$Role is not a full lowercase commit SHA."
    }
}

function New-DevNasHierarchyEvidenceIntegrity(
    [string] $EvidenceDirectory,
    [string] $PreMigrationRuntimeSha,
    [string] $ApplyCommitSha) {
    Assert-DevNasHierarchySha $PreMigrationRuntimeSha 'Pre-migration runtime SHA'
    Assert-DevNasHierarchySha $ApplyCommitSha 'Apply commit SHA'
    $evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
    $files = foreach ($name in $script:DevNasHierarchyEvidenceFiles) {
        $path = Join-Path $evidence $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Cannot seal incomplete hierarchy evidence: $name"
        }
        $item = Get-Item -LiteralPath $path
        [ordered]@{
            Name = $name
            Length = [long]$item.Length
            Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $integrity = [ordered]@{
        SchemaVersion = $script:DevNasHierarchyEvidenceSchemaVersion
        EvidenceDirectory = $evidence
        PreMigrationRuntimeSha = $PreMigrationRuntimeSha
        ApplyCommitSha = $ApplyCommitSha
        Files = @($files)
    }
    $target = Join-Path $evidence 'evidence-integrity.json'
    $temporary = $target + '.tmp'
    $integrity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $target -Force
    return [pscustomobject]$integrity
}

function Read-DevNasHierarchyEvidence([string] $EvidenceDirectory) {
    $evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
    $integrityPath = Join-Path $evidence 'evidence-integrity.json'
    if (-not (Test-Path -LiteralPath $integrityPath -PathType Leaf)) {
        throw 'Rollback evidence is incomplete: evidence-integrity.json'
    }
    $integrity = Get-Content -LiteralPath $integrityPath -Raw | ConvertFrom-Json
    if ([int]$integrity.SchemaVersion -ne $script:DevNasHierarchyEvidenceSchemaVersion -or
        [IO.Path]::GetFullPath([string]$integrity.EvidenceDirectory) -ne $evidence) {
        throw 'Rollback evidence integrity metadata is invalid.'
    }
    Assert-DevNasHierarchySha ([string]$integrity.PreMigrationRuntimeSha) 'Evidence pre-migration runtime SHA'
    Assert-DevNasHierarchySha ([string]$integrity.ApplyCommitSha) 'Evidence Apply commit SHA'
    $records = @($integrity.Files)
    if ($records.Count -ne $script:DevNasHierarchyEvidenceFiles.Count -or
        @($records | Group-Object Name | Where-Object Count -ne 1).Count -ne 0) {
        throw 'Rollback evidence integrity file set is invalid.'
    }
    foreach ($name in $script:DevNasHierarchyEvidenceFiles) {
        $record = @($records | Where-Object Name -eq $name)
        if ($record.Count -ne 1) { throw "Rollback evidence is incomplete: $name" }
        $path = Join-Path $evidence $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Rollback evidence is incomplete: $name"
        }
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$record[0].Length -or
            $hash -ne ([string]$record[0].Sha256).ToLowerInvariant()) {
            throw "Rollback evidence integrity verification failed: $name"
        }
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'summary.json') -Raw | ConvertFrom-Json
    if ([string]$summary.PreMigrationRuntimeSha -ne [string]$integrity.PreMigrationRuntimeSha -or
        [string]$summary.ApplyCommitSha -ne [string]$integrity.ApplyCommitSha) {
        throw 'Rollback evidence version metadata does not match its integrity seal.'
    }
    return [pscustomobject]@{ Summary=$summary; Integrity=$integrity }
}

function Get-DevNasHierarchyRollbackMode(
    $Evidence,
    [string] $ApprovedApplyCommitSha,
    [string] $ApprovedCurrentRuntimeSha) {
    Assert-DevNasHierarchySha $ApprovedApplyCommitSha 'Approved Apply commit SHA'
    Assert-DevNasHierarchySha $ApprovedCurrentRuntimeSha 'Approved current runtime SHA'
    if ([string]$Evidence.Integrity.ApplyCommitSha -ne $ApprovedApplyCommitSha) {
        throw 'Approved Apply commit SHA does not match the sealed evidence.'
    }
    $preMigration = [string]$Evidence.Integrity.PreMigrationRuntimeSha
    if ($ApprovedCurrentRuntimeSha -eq $preMigration) { return 'BeforeDeployment' }
    if ($ApprovedCurrentRuntimeSha -eq $ApprovedApplyCommitSha) { return 'AfterDeployment' }
    throw 'Approved current runtime SHA is unrelated to this hierarchy transition.'
}
