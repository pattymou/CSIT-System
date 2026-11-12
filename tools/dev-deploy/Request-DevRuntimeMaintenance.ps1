param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Sha,
    [Parameter(Mandatory)] [ValidateRange(1,2147483647)] [int] $Pid,
    [ValidateRange(1,15)] [int] $ExpiresMinutes = 10,
    [string] $Reason = 'Approved DEV runtime configuration maintenance'
)

$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$stateRoot = Join-Path $root 'state'
$currentFile = Join-Path $stateRoot 'current.json'
$requestFile = Join-Path $stateRoot 'runtime-maintenance-request.json'
$lock = $null

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may create a DEV maintenance request.'
}
if (-not (Test-Path -LiteralPath $currentFile)) { throw 'DEV current state is unavailable.' }
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
if ([string]$current.Sha -ne $Sha -or [int]$current.Pid -ne $Pid) {
    throw 'The requested DEV SHA or PID does not match current.json.'
}
$expectedPath = Join-Path (Join-Path $root 'releases') $Sha
if ([IO.Path]::GetFullPath([string]$current.Path).TrimEnd('\') -ne
    [IO.Path]::GetFullPath($expectedPath).TrimEnd('\')) {
    throw 'The DEV release path is not the SHA-scoped release.'
}
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$Pid"
$expectedExe = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'
if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
    [IO.Path]::GetFullPath([string]$process.ExecutablePath) -ne [IO.Path]::GetFullPath($expectedExe)) {
    throw 'The requested DEV PID is not the recorded release executable.'
}
$productionBefore = @(Get-NetTCPConnection -State Listen -LocalPort 5010 |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($productionBefore.Count -ne 1) { throw 'Production 5010 PID guard failed.' }
try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'deploy.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] { throw 'A DEV deployment or maintenance operation is already running.' }
try {
    $now = (Get-Date).ToUniversalTime()
    $request = [ordered]@{
        Action = 'ReloadRuntimeConfiguration'; Sha = $Sha; Pid = $Pid
        Path = [string]$current.Path; RequestedBy = $identity.Name
        RequestedAtUtc = $now.ToString('o'); ExpiresAtUtc = $now.AddMinutes($ExpiresMinutes).ToString('o')
        Reason = if ($Reason.Length -gt 256) { $Reason.Substring(0,256) } else { $Reason }
    }
    $tmp = $requestFile + '.tmp'
    $request | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding UTF8
    & icacls.exe $tmp /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Maintenance request ACL failed.' }
    Move-Item -LiteralPath $tmp -Destination $requestFile -Force
    & icacls.exe $requestFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Maintenance request ACL verification failed.' }
    if (@(Get-NetTCPConnection -State Listen -LocalPort 5010 |
        Select-Object -ExpandProperty OwningProcess -Unique)[0] -ne $productionBefore[0]) {
        throw 'Production 5010 PID changed while creating maintenance request.'
    }
    Write-Output ('DEV_MAINTENANCE_REQUESTED sha=' + $Sha + ' pid=' + $Pid +
        ' expires=' + $request.ExpiresAtUtc)
} finally { if ($lock) { $lock.Dispose() } }
