$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevMigrationReview.ps1')

function Assert-ThrowsLike([scriptblock] $Action, [string] $Pattern, [string] $Case) {
    try { & $Action; throw "Expected failure was not raised: $Case" }
    catch {
        if ([string] $_.Exception.Message -notmatch $Pattern) { throw }
    }
    Write-Output ('PASS ' + $Case)
}

$sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$otherSha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$migrations = @('20261024033553_LinkVerificationApplicationAttachments',
    '20261024090000_PreventDuplicateVerificationApplicationFileNames')
$root = Join-Path ([IO.Path]::GetTempPath()) ('csit-dev-migration-review-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $changed = @('Migrations/20261024033553_LinkVerificationApplicationAttachments.cs',
        'Migrations/20261024033553_LinkVerificationApplicationAttachments.Designer.cs',
        'Migrations/20261024090000_PreventDuplicateVerificationApplicationFileNames.cs',
        'Migrations/AppDbContextModelSnapshot.cs', 'tools/dev-deploy/DevMigrationReview.ps1')
    if (-not (Test-DevMigrationChanges -ChangedPaths $changed) -or
        @(Get-RequiredDevMigrationIds -ChangedPaths $changed).Count -ne 2) {
        throw 'Migration change discovery failed.'
    }
    Write-Output 'PASS migration discovery'

    $markerPath = Get-DevMigrationReviewMarkerPath -ReviewRoot $root -Sha $sha
    @{
        SchemaVersion = 1; Sha = $sha; Database = 'sit_department_system_dev';
        Migrations = $migrations; ReviewedBy = 'BUILTIN\Administrators';
        ReviewedAtUtc = '2026-10-24T04:30:00Z'
    } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
    $null = Assert-DevMigrationReviewMarkerContent -MarkerPath $markerPath -Sha $sha `
        -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
        -AppliedMigrationIds $migrations
    Write-Output 'PASS valid marker'

    $missing = Get-DevMigrationReviewMarkerPath -ReviewRoot $root -Sha $otherSha
    Assert-ThrowsLike {
        Assert-DevMigrationReviewMarkerContent -MarkerPath $missing -Sha $otherSha `
            -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
            -AppliedMigrationIds $migrations
    } '^DEV_MIGRATION_REVIEW_REQUIRED:' 'missing marker'

    $badShaMarker = Get-DevMigrationReviewMarkerPath -ReviewRoot $root -Sha $otherSha
    Copy-Item -LiteralPath $markerPath -Destination $badShaMarker
    Assert-ThrowsLike {
        Assert-DevMigrationReviewMarkerContent -MarkerPath $badShaMarker -Sha $otherSha `
            -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
            -AppliedMigrationIds $migrations
    } '^DEV_MIGRATION_REVIEW_INVALID:' 'SHA mismatch'

    Assert-ThrowsLike {
        Assert-DevMigrationReviewMarkerContent -MarkerPath $markerPath -Sha $sha `
            -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
            -AppliedMigrationIds @($migrations[0])
    } '^DEV_MIGRATION_REVIEW_INVALID:' 'incomplete migration history'

    $null = Assert-DevMigrationReviewMarkerContent -MarkerPath $markerPath -Sha $sha `
        -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
        -AppliedMigrationIds $migrations
    $null = Assert-DevMigrationReviewMarkerContent -MarkerPath $markerPath -Sha $sha `
        -Database 'sit_department_system_dev' -RequiredMigrationIds $migrations `
        -AppliedMigrationIds $migrations
    Write-Output 'PASS exact-SHA marker repeat use'

    $reviewFailure = [pscustomobject]@{Sha=$sha; Tag='dev-ready-test';
        Reason='DEV_MIGRATION_REVIEW_REQUIRED: review required.'}
    if (-not (Test-DevMigrationReviewRetryAllowed -Failed $reviewFailure -ReviewRoot $root)) {
        throw 'Reviewed migration failure was not retryable.'
    }
    $otherFailure = [pscustomobject]@{Sha=$sha; Tag='dev-ready-test'; Reason='DEV tests failed.'}
    if (Test-DevMigrationReviewRetryAllowed -Failed $otherFailure -ReviewRoot $root) {
        throw 'Unrelated failure became retryable.'
    }
    Remove-Item -LiteralPath $markerPath
    if (Test-DevMigrationReviewRetryAllowed -Failed $reviewFailure -ReviewRoot $root) {
        throw 'Migration failure retried without its exact marker.'
    }
    Write-Output 'PASS controlled retry and redeploy'
} finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ([IO.Path]::GetFullPath($root).StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}
