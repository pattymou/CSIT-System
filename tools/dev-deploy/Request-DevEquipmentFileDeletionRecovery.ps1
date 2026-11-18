param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F-]{36}$')] [string] $JobId,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Sha,
    [Parameter(Mandatory)] [Alias('Pid')] [ValidateRange(1,2147483647)] [int] $RuntimePid,
    [ValidateRange(1,10)] [int] $ExpiresMinutes = 5,
    [string] $Reason = 'KM DEV equipment identifier contract repaired; approved single-job recovery'
)

$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'; $stateRoot = Join-Path $root 'state'
$currentFile = Join-Path $stateRoot 'current.json'
$requestFile = Join-Path $stateRoot 'runtime-equipment-file-deletion-recovery-request.json'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may create an equipment deletion recovery request.'
}
$job = [guid]$JobId
if ($job -eq [guid]::Empty) { throw 'JobId must be a non-empty GUID.' }
$trimmedReason = $Reason.Trim()
if ($trimmedReason.Length -lt 5 -or $trimmedReason.Length -gt 500 -or
    $trimmedReason -match '[\x00-\x1F\x7F]' -or
    $trimmedReason -match '(?i)authorization:|bearer\s|password|client_secret|access_token') {
    throw 'The recovery reason is invalid or contains credential-like content.'
}
if (-not (Test-Path -LiteralPath $currentFile)) { throw 'DEV current state is unavailable.' }
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
if ([string]$current.Sha -ne $Sha -or [int]$current.Pid -ne $RuntimePid) {
    throw 'The requested DEV SHA or PID does not match current.json.'
}
$expectedPath = Join-Path (Join-Path $root 'releases') $Sha
if ([IO.Path]::GetFullPath([string]$current.Path).TrimEnd('\') -ne
    [IO.Path]::GetFullPath($expectedPath).TrimEnd('\')) {
    throw 'The DEV release path is not the SHA-scoped release.'
}
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$RuntimePid"
$expectedExe = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'
if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
    [IO.Path]::GetFullPath([string]$process.ExecutablePath) -ne
        [IO.Path]::GetFullPath($expectedExe)) {
    throw 'The requested DEV PID is not the recorded release executable.'
}
$productionBefore = @(Get-NetTCPConnection -State Listen -LocalPort 5010 |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($productionBefore.Count -ne 1) { throw 'Production 5010 PID guard failed.' }
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'deploy.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] { throw 'A DEV deployment or maintenance operation is already running.' }
try {
    if (Test-Path -LiteralPath $requestFile) {
        throw 'An equipment deletion recovery request already exists.'
    }
    $now = (Get-Date).ToUniversalTime()
    $request = [ordered]@{
        Action='RecoverEquipmentFileDeletion'; Sha=$Sha; Pid=$RuntimePid
        Path=[string]$current.Path; JobId=$job.ToString('D')
        RequestedBy=$identity.Name; Reason=$trimmedReason
        RequestedAtUtc=$now.ToString('o')
        ExpiresAtUtc=$now.AddMinutes($ExpiresMinutes).ToString('o')
    }
    $tmp = $requestFile + '.tmp'
    $request | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding UTF8
    & icacls.exe $tmp /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' `
        '*S-1-5-19:(R,D)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Equipment deletion recovery request ACL failed.' }
    Move-Item -LiteralPath $tmp -Destination $requestFile
    if (@(Get-NetTCPConnection -State Listen -LocalPort 5010 |
        Select-Object -ExpandProperty OwningProcess -Unique)[0] -ne $productionBefore[0]) {
        throw 'Production 5010 PID changed while creating the recovery request.'
    }
    Write-Output ('DEV_EQUIPMENT_FILE_DELETION_RECOVERY_REQUESTED job=' +
        $job.ToString('D') + ' sha=' + $Sha + ' pid=' + $RuntimePid)
} finally { if ($lock) { $lock.Dispose() } }
