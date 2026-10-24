param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Sha,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]{14}_[A-Za-z0-9_]+$')]
    [string[]] $MigrationId
)

$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$reviewRoot = Join-Path $root 'migration-reviews'
$secretFile = Join-Path $root 'secrets\runtime.json'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
$git = 'C:\Program Files\Git\cmd\git.exe'
$remote = 'https://github.com/pattymou/CSIT-System.git'
$branch = 'feature/km-stage-integration'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may approve a DEV migration review.'
}
$remoteSha = ((& $git ls-remote $remote "refs/heads/$branch") -split '\s+')[0]
if ($LASTEXITCODE -ne 0 -or $remoteSha -cne $Sha) {
    throw 'The reviewed SHA is not the current designated DEV branch head.'
}
if (-not (Test-Path -LiteralPath $secretFile)) { throw 'The protected DEV runtime configuration is absent.' }
$secrets = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
$connection.set_ConnectionString([string] $secrets.PSObject.Properties['ConnectionStrings:Postgres'].Value)
if ([string] $connection['Database'] -ne 'sit_department_system_dev' -or
    [string] $connection['Username'] -ne 'csit_runtime_dev') {
    throw 'The protected connection does not target the DEV database and runtime role.'
}
$ids = @($MigrationId | Sort-Object -Unique)
$quoted = @($ids | ForEach-Object { "'$_'" }) -join ','
$query = 'SELECT "MigrationId" FROM "__EFMigrationsHistory" WHERE "MigrationId" IN (' +
    $quoted + ') ORDER BY "MigrationId";'
$env:PGPASSWORD = [string] $connection['Password']
try {
    $applied = @($query | & $psql -X -w -t -A -v ON_ERROR_STOP=1 `
        -h ([string] $connection['Host']) -p ([string] $connection['Port']) `
        -U ([string] $connection['Username']) -d ([string] $connection['Database']) |
        ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0 -or @($ids | Where-Object { $applied -notcontains $_ }).Count -gt 0) {
        throw 'One or more approved Migrations are absent from sit_department_system_dev.'
    }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
& icacls.exe $reviewRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)R' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the DEV migration review directory.' }
$markerPath = Join-Path $reviewRoot ($Sha + '.json')
if (Test-Path -LiteralPath $markerPath) {
    throw 'The exact-SHA DEV migration review marker already exists.'
}
$temporaryMarker = Join-Path $reviewRoot ($Sha + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
@{
    SchemaVersion = 1
    Sha = $Sha
    Database = 'sit_department_system_dev'
    Migrations = $ids
    ReviewedBy = $identity.Name
    ReviewedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporaryMarker -Encoding UTF8
& icacls.exe $temporaryMarker /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $temporaryMarker -Force -ErrorAction SilentlyContinue
    throw 'Could not protect the DEV migration review marker.'
}
Move-Item -LiteralPath $temporaryMarker -Destination $markerPath
Write-Output ('DEV_MIGRATION_REVIEW_APPROVED sha=' + $Sha + ' database=sit_department_system_dev migrations=' +
    ($ids -join ','))
