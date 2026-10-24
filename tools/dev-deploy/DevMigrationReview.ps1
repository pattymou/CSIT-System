function Get-DevMigrationReviewMarkerPath {
    param(
        [Parameter(Mandatory = $true)][string] $ReviewRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string] $Sha
    )
    return Join-Path $ReviewRoot ($Sha + '.json')
}

function Test-DevMigrationChanges {
    param([Parameter(Mandatory = $true)][string[]] $ChangedPaths)
    return @($ChangedPaths | Where-Object {
        $_ -match '(^|/)(Migrations|migrations)(/|$)|migration|\.sql$'
    }).Count -gt 0
}

function Get-RequiredDevMigrationIds {
    param([Parameter(Mandatory = $true)][string[]] $ChangedPaths)
    return @($ChangedPaths | ForEach-Object {
        if ($_ -match '^(?:.*/)?Migrations/([0-9]{14}_[A-Za-z0-9_]+)(?:\.Designer)?\.cs$') {
            $Matches[1]
        }
    } | Sort-Object -Unique)
}

function Assert-DevMigrationReviewMarkerAcl {
    param([Parameter(Mandatory = $true)][string] $MarkerPath)
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        throw 'DEV_MIGRATION_REVIEW_REQUIRED: the exact-SHA DBA review marker is absent.'
    }
    $acl = Get-Acl -LiteralPath $MarkerPath
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $localServiceSid = 'S-1-5-19'
    $allowedWriterSids = @('S-1-5-18', 'S-1-5-32-544')
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in $rules | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($_.FileSystemRights -band $writeRights) -ne 0 }) {
        $sid = $rule.IdentityReference.Value
        if ($sid -eq $localServiceSid -or $allowedWriterSids -notcontains $sid) {
            throw 'DEV_MIGRATION_REVIEW_INVALID: the marker ACL permits an unauthorized writer.'
        }
    }
    if (@($rules | Where-Object {
            $_.IdentityReference.Value -eq $localServiceSid -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne 0
        }).Count -eq 0) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: LOCAL SERVICE cannot read the marker.'
    }
}

function Assert-DevMigrationReviewMarkerContent {
    param(
        [Parameter(Mandatory = $true)][string] $MarkerPath,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string] $Sha,
        [Parameter(Mandatory = $true)][string] $Database,
        [Parameter(Mandatory = $true)][string[]] $RequiredMigrationIds,
        [Parameter(Mandatory = $true)][string[]] $AppliedMigrationIds
    )
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        throw 'DEV_MIGRATION_REVIEW_REQUIRED: the exact-SHA DBA review marker is absent.'
    }
    try {
        $marker = Get-Content -LiteralPath $MarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        throw 'DEV_MIGRATION_REVIEW_INVALID: the marker is not valid JSON.'
    }
    if ([string] $marker.SchemaVersion -ne '1' -or [string] $marker.Sha -cne $Sha -or
        [string] $marker.Database -cne $Database) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: marker schema, SHA, or database does not match.'
    }
    if ([string]::IsNullOrWhiteSpace([string] $marker.ReviewedBy) -or
        [string]::IsNullOrWhiteSpace([string] $marker.ReviewedAtUtc)) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: reviewer metadata is incomplete.'
    }
    try { $null = [DateTimeOffset]::Parse([string] $marker.ReviewedAtUtc) }
    catch { throw 'DEV_MIGRATION_REVIEW_INVALID: review timestamp is invalid.' }

    $required = @($RequiredMigrationIds | Sort-Object -Unique)
    $approved = @($marker.Migrations | ForEach-Object { [string] $_ } | Sort-Object -Unique)
    if ($required.Count -eq 0 -or @($required | Where-Object {
            $_ -notmatch '^[0-9]{14}_[A-Za-z0-9_]+$'
        }).Count -gt 0 -or
        @(Compare-Object -ReferenceObject $required -DifferenceObject $approved).Count -ne 0) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: approved Migration IDs do not exactly match the release.'
    }
    $applied = @($AppliedMigrationIds | Sort-Object -Unique)
    if (@($required | Where-Object { $applied -notcontains $_ }).Count -gt 0) {
        throw 'DEV_MIGRATION_REVIEW_INVALID: an approved Migration is absent from DEV migration history.'
    }
    return $marker
}

function Test-DevMigrationReviewRetryAllowed {
    param(
        [Parameter(Mandatory = $true)] $Failed,
        [Parameter(Mandatory = $true)][string] $ReviewRoot
    )
    $sha = [string] $Failed.Sha
    if ($sha -notmatch '^[0-9a-f]{40}$' -or
        [string] $Failed.Reason -notmatch '^DEV_MIGRATION_REVIEW_REQUIRED:' ) {
        return $false
    }
    return Test-Path -LiteralPath (Get-DevMigrationReviewMarkerPath -ReviewRoot $ReviewRoot -Sha $sha) -PathType Leaf
}
