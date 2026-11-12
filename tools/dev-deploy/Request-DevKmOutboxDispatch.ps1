param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F-]{36}$')] [string] $OutboxId,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F-]{36}$')] [string] $EventId,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Sha,
    [Parameter(Mandatory)] [Alias('Pid')] [ValidateRange(1,2147483647)] [int] $RuntimePid,
    [ValidateRange(1,10)] [int] $ExpiresMinutes = 5,
    [string] $Reason = 'Approved one-shot DEV verification attachment KM Gate 1 dispatch'
)

$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'; $stateRoot = Join-Path $root 'state'
$currentFile = Join-Path $stateRoot 'current.json'
$requestFile = Join-Path $stateRoot 'runtime-km-outbox-dispatch-request.json'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may create a DEV KM dispatch request.'
}
$outbox = [guid]$OutboxId; $event = [guid]$EventId
if ($outbox -eq [guid]::Empty -or $event -eq [guid]::Empty) { throw 'OutboxId and EventId must be non-empty GUIDs.' }
if (-not (Test-Path -LiteralPath $currentFile)) { throw 'DEV current state is unavailable.' }
$current = Get-Content -LiteralPath $currentFile -Raw | ConvertFrom-Json
if ([string]$current.Sha -ne $Sha -or [int]$current.Pid -ne $RuntimePid) {
    throw 'The requested DEV SHA or PID does not match current.json.'
}
$expectedPath = Join-Path (Join-Path $root 'releases') $Sha
if ([IO.Path]::GetFullPath([string]$current.Path).TrimEnd('\') -ne [IO.Path]::GetFullPath($expectedPath).TrimEnd('\')) {
    throw 'The DEV release path is not the SHA-scoped release.'
}
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$RuntimePid"
$expectedExe = Join-Path ([string]$current.Path) 'SIT.DepartmentSystem.Web.exe'
if ($null -eq $process -or $process.Name -ne 'SIT.DepartmentSystem.Web.exe' -or
    [IO.Path]::GetFullPath([string]$process.ExecutablePath) -ne [IO.Path]::GetFullPath($expectedExe)) {
    throw 'The requested DEV PID is not the recorded release executable.'
}
$productionBefore = @(Get-NetTCPConnection -State Listen -LocalPort 5010 |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($productionBefore.Count -ne 1) { throw 'Production 5010 PID guard failed.' }
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'deploy.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] { throw 'A DEV deployment or maintenance operation is already running.' }
try {
    $now = (Get-Date).ToUniversalTime()
    $request = [ordered]@{ Action='DispatchVerificationAttachment'; Sha=$Sha; Pid=$RuntimePid;
        Path=[string]$current.Path; OutboxId=$outbox.ToString('D'); EventId=$event.ToString('D');
        RequestedBy=$identity.Name; RequestedAtUtc=$now.ToString('o');
        ExpiresAtUtc=$now.AddMinutes($ExpiresMinutes).ToString('o');
        Reason=if ($Reason.Length -gt 256) { $Reason.Substring(0,256) } else { $Reason } }
    $tmp = $requestFile + '.tmp'; $request | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding UTF8
    & icacls.exe $tmp /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'KM dispatch request ACL failed.' }
    Move-Item -LiteralPath $tmp -Destination $requestFile -Force
    & icacls.exe $requestFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'KM dispatch request ACL verification failed.' }
    if (@(Get-NetTCPConnection -State Listen -LocalPort 5010 |
        Select-Object -ExpandProperty OwningProcess -Unique)[0] -ne $productionBefore[0]) {
        throw 'Production 5010 PID changed while creating KM dispatch request.'
    }
    Write-Output ('DEV_KM_OUTBOX_DISPATCH_REQUESTED outbox=' + $outbox.ToString('D') +
        ' event=' + $event.ToString('D') + ' sha=' + $Sha + ' pid=' + $RuntimePid)
} finally { if ($lock) { $lock.Dispose() } }
