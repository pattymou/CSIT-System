$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Reconcile-DevNasBusinessHierarchy.ps1') `
    -TechnicalRoot '\\AI-NAS\Workspace_B\CSIT_TECH_DATA_DEV' -LoadFunctionsOnly

$passed = 0
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('csit-nas-reentry-' + [guid]::NewGuid().ToString('N'))
$priorCommit = '519aa2a11c511bf8c26a1f5fe4b5661da0bb92fe'
$newCommit = '8907aff4fe3b4e1fce540b1193e5cbe7546991ee'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock] $Action, [string] $Pattern) {
    try { & $Action; throw "Expected failure matching: $Pattern" }
    catch {
        if ($_.Exception.Message -notlike "*$Pattern*") { throw }
    }
}

try {
    $maintenanceRoot = Join-Path $fixtureRoot 'maintenance'
    $nasRoot = Join-Path $fixtureRoot 'formal'
    $TechnicalRoot = Join-Path $fixtureRoot 'technical'
    $newRawRoot = Join-Path $TechnicalRoot 'RawData'
    $stateRoot = Join-Path $fixtureRoot 'state'
    foreach ($path in @($maintenanceRoot,$nasRoot,$newRawRoot,$stateRoot)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    $runtimePath = Join-Path $fixtureRoot 'runtime.json'
    '{"mode":"legacy"}' | Set-Content -LiteralPath $runtimePath -Encoding UTF8
    $priorEvidence = Join-Path $maintenanceRoot 'prior-failure'
    New-Item -ItemType Directory -Path $priorEvidence | Out-Null
    Copy-Item -LiteralPath $runtimePath -Destination (Join-Path $priorEvidence 'runtime.before.json')
    '{}' | Set-Content -LiteralPath (Join-Path $priorEvidence 'current.before.json') -Encoding UTF8
    'BEGIN;' | Set-Content -LiteralPath (Join-Path $priorEvidence 'database-switch.sql') -Encoding UTF8
    'BEGIN;' | Set-Content -LiteralPath (Join-Path $priorEvidence 'database-rollback.sql') -Encoding UTF8

    $manifestRows = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 155; $index++) {
        $technical = $index -ge 7
        $kind = if ($technical) { 'TechnicalRawData' } else { 'Document' }
        $source = if ($technical) {
            Join-Path $nasRoot ("RawData\source-$index.bin")
        } else {
            Join-Path $nasRoot ("DocumentLibrary\source-$index.bin")
        }
        $target = if ($technical) {
            Join-Path $newRawRoot ("target-$index.bin")
        } else {
            Join-Path $nasRoot ("分類\L2\L3\target-$index.bin")
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $source) -Force | Out-Null
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $bytes = [Text.Encoding]::UTF8.GetBytes("fixture-$index")
        [IO.File]::WriteAllBytes($source, $bytes)
        [IO.File]::WriteAllBytes($target, $bytes)
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestRows.Add([pscustomobject]@{
            Kind=$kind; Key=("record-$index"); SourcePath=$source; TargetPath=$target
            Size=$bytes.Length; Sha256=$hash; DbValue=("legacy-$index");
            NewDbValue=("new-$index")
        })
    }
    $manifestPath = Join-Path $priorEvidence 'file-manifest.csv'
    $manifestRows | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8
    $manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $legacyBackup = Join-Path $TechnicalRoot 'LegacySourceBackup\prior-failure'
    [ordered]@{
        Mode='Apply'; PreMigrationRuntimeSha='1a9c6bbe00ebfc1a386eae67222e28567bf1b842'
        ApplyCommitSha=$priorCommit; EvidenceDirectory=$priorEvidence
        LegacySourceBackup=$legacyBackup
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $priorEvidence 'summary.json') -Encoding UTF8
    $fencePath = Join-Path $stateRoot 'nas-business-hierarchy-transition.json'
    $fence = [pscustomobject]@{
        SchemaVersion=1; Status='RolledBack'
        PreMigrationRuntimeSha='1a9c6bbe00ebfc1a386eae67222e28567bf1b842'
        ApplyCommitSha=$priorCommit; EvidenceDirectory=$priorEvidence
    }
    $fence | ConvertTo-Json | Set-Content -LiteralPath $fencePath -Encoding UTF8
    $current = [pscustomobject]@{ Sha=$fence.PreMigrationRuntimeSha; Pid=1234 }
    $currentManifest = @(Import-Csv -LiteralPath $manifestPath)

    $result = Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath
    Assert-True ($result.Manifest.Count -eq 155) 'Valid RolledBack re-entry was rejected.'
    $passed++; Write-Output 'PASS valid_rolledback_reentry'

    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit ('0' * 64) `
        $current $currentManifest $runtimePath } 'manifest SHA256'
    $passed++; Write-Output 'PASS prior_evidence_sha_mismatch_rejected'

    $currentManifest[0].DbValue = 'unexpected-db-value'
    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'current DB/source manifest differs'
    $currentManifest[0].DbValue = $manifestRows[0].DbValue
    '{"mode":"changed"}' | Set-Content -LiteralPath $runtimePath -Encoding UTF8
    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'runtime configuration'
    Copy-Item -LiteralPath (Join-Path $priorEvidence 'runtime.before.json') `
        -Destination $runtimePath -Force
    $passed++; Write-Output 'PASS db_or_runtime_mismatch_rejected'

    [IO.File]::WriteAllText($manifestRows[0].SourcePath, 'changed')
    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'source SHA256'
    [IO.File]::WriteAllBytes($manifestRows[0].SourcePath,
        [Text.Encoding]::UTF8.GetBytes('fixture-0'))
    [IO.File]::WriteAllText($manifestRows[1].TargetPath, 'changed')
    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'target SHA256'
    [IO.File]::WriteAllBytes($manifestRows[1].TargetPath,
        [Text.Encoding]::UTF8.GetBytes('fixture-1'))
    $passed++; Write-Output 'PASS source_or_target_sha_mismatch_rejected'

    $extra = Join-Path $newRawRoot 'unclassified.bin'
    [IO.File]::WriteAllBytes($extra, [byte[]](1,2,3))
    Assert-Fails { Assert-ReentryState $fence $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'unclassified file'
    Remove-Item -LiteralPath $extra
    $passed++; Write-Output 'PASS unclassified_target_rejected'

    $newEvidence = Join-Path $maintenanceRoot 'new-transaction'
    New-Item -ItemType Directory -Path $newEvidence | Out-Null
    $oldFenceHash = (Get-FileHash -LiteralPath $fencePath -Algorithm SHA256).Hash
    $relation = New-ReentryPredecessorArchive $fencePath $newEvidence $priorEvidence `
        $priorCommit $manifestSha $newCommit
    $newFence = [ordered]@{
        SchemaVersion=1; Status='AwaitingCompatibleDeployment'
        PreMigrationRuntimeSha=$current.Sha; ApplyCommitSha=$newCommit
        EvidenceDirectory=$newEvidence; ReentryPredecessor=$relation
    }
    Set-TransitionFenceAtomically $fencePath $oldFenceHash $newFence
    $active = Get-Content -LiteralPath $fencePath -Raw | ConvertFrom-Json
    Assert-Fails { Assert-ReentryState $active $priorEvidence $priorCommit $manifestSha `
        $current $currentManifest $runtimePath } 'RolledBack fence'
    $passed++; Write-Output 'PASS repeated_reentry_rejected'

    $watchLockPath = Join-Path $stateRoot 'watch.lock'
    $deployLockPath = Join-Path $stateRoot 'deploy.lock'
    $heldWatch = [IO.File]::Open($watchLockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Assert-Fails { Enter-DevNasMaintenanceLocks $watchLockPath $deployLockPath } 'watcher is active' }
    finally { $heldWatch.Dispose() }
    $heldDeploy = [IO.File]::Open($deployLockPath,[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Assert-Fails { Enter-DevNasMaintenanceLocks $watchLockPath $deployLockPath } 'maintenance operation' }
    finally { $heldDeploy.Dispose() }
    $acquired = Enter-DevNasMaintenanceLocks $watchLockPath $deployLockPath
    $acquired.Deployment.Dispose()
    $acquired.Watcher.Dispose()
    $passed++; Write-Output 'PASS dual_lock_conflict_rejected'

    Copy-Item -LiteralPath (Join-Path $newEvidence 'predecessor-fence.json') `
        -Destination $fencePath -Force
    $interruptEvidence = Join-Path $maintenanceRoot 'interrupted-initialization'
    New-Item -ItemType Directory -Path $interruptEvidence | Out-Null
    $beforeArchiveHash = (Get-FileHash -LiteralPath $fencePath -Algorithm SHA256).Hash
    $null = New-ReentryPredecessorArchive $fencePath $interruptEvidence $priorEvidence `
        $priorCommit $manifestSha $newCommit
    Assert-True ((Get-FileHash -LiteralPath $fencePath -Algorithm SHA256).Hash -eq
        $beforeArchiveHash) 'Initialization archive changed the active RolledBack fence.'
    $passed++; Write-Output 'PASS interrupted_initialization_keeps_old_fence'

    $trace = Get-Content -LiteralPath (Join-Path $interruptEvidence 'reentry-predecessor.json') `
        -Raw | ConvertFrom-Json
    Assert-True ($trace.PriorFenceSha256 -eq $beforeArchiveHash -and
        $trace.PriorManifestSha256 -eq $manifestSha -and
        $trace.PriorApplyCommitSha -eq $priorCommit -and
        $trace.NewApplyCommitSha -eq $newCommit) 'Re-entry traceability record is incomplete.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $interruptEvidence `
        'predecessor-fence.json') -Algorithm SHA256).Hash -eq $beforeArchiveHash) `
        'Archived predecessor fence does not match the active fence.'
    $passed++; Write-Output 'PASS new_transaction_traces_prior_evidence'

    foreach ($name in @('file-manifest.csv','database-switch.sql','database-rollback.sql',
            'runtime.before.json','current.before.json')) {
        Copy-Item -LiteralPath (Join-Path $priorEvidence $name) `
            -Destination (Join-Path $interruptEvidence $name)
    }
    [ordered]@{
        PreMigrationRuntimeSha=$current.Sha; ApplyCommitSha=$newCommit
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $interruptEvidence 'summary.json') `
        -Encoding UTF8
    $null = New-DevNasHierarchyEvidenceIntegrity $interruptEvidence $current.Sha $newCommit
    $sealed = Read-DevNasHierarchyEvidence $interruptEvidence
    $sealedNames = @($sealed.Integrity.Files | ForEach-Object Name)
    Assert-True ($sealedNames -contains 'predecessor-fence.json' -and
        $sealedNames -contains 'reentry-predecessor.json') `
        'Sealed re-entry evidence omitted predecessor traceability artifacts.'
    $passed++; Write-Output 'PASS reentry_evidence_seals_traceability'

    $unicodeManifest = @([pscustomobject]@{
        Kind='Document'; Key='11111111-1111-1111-1111-111111111111'
        DbValue='舊路徑\文件'; NewDbValue='新路徑\文件'
    })
    $forward = New-DatabaseSwitchSql $unicodeManifest
    $rollback = New-DatabaseSwitchSql $unicodeManifest -Rollback
    Assert-True ($OutputEncoding.WebName -eq 'utf-8' -and
        $forward.Contains('新路徑\文件') -and $rollback.Contains('舊路徑\文件')) `
        'Forward/rollback SQL lost UTF-8 path support.'
    $passed++; Write-Output 'PASS forward_and_rollback_sql_remain_utf8'

    if ($passed -ne 11) { throw "Unexpected test count: $passed" }
    Write-Output "DEV_NAS_REENTRY_TEST_PASS count=$passed"
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
