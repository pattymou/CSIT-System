[CmdletBinding()]
param(
    [string] $NasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV',
    [ValidateRange(10, 120)]
    [int] $TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$devRoot = 'C:\ProgramData\CSIT-DEV'
$evidenceRoot = Join-Path $devRoot 'maintenance'
$expectedNasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'

function Get-SingleListenerPid([int] $Port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -ne 1) { throw "Port $Port must have exactly one listener." }
    return [int] $listeners[0]
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Only a local Administrator may request the LOCAL SERVICE DEV NAS probe.'
}
if (-not $NasRoot.Equals($expectedNasRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The probe is restricted to the approved DEV NAS root: $expectedNasRoot"
}
if (-not (Test-Path -LiteralPath $evidenceRoot -PathType Container)) {
    throw 'The protected DEV maintenance directory is unavailable.'
}

$productionPid = Get-SingleListenerPid 5010
$devPid = Get-SingleListenerPid 7444
$devProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$devPid"
$devOwner = Invoke-CimMethod -InputObject $devProcess -MethodName GetOwner
if (($devOwner.Domain + '\' + $devOwner.User) -ne 'NT AUTHORITY\LOCAL SERVICE') {
    throw 'The current DEV runtime is not running as LOCAL SERVICE.'
}

$probeId = [Guid]::NewGuid().ToString('N')
$taskName = "CSIT DEV NAS Probe $probeId"
$target = Join-Path $NasRoot ".csit-dev-probe-$probeId.tmp"
$evidence = Join-Path $evidenceRoot "nas-access-probe-$probeId.json"
$probeScript = @"
`$ErrorActionPreference = 'Stop'
`$result = [ordered]@{
    ProbeId = '$probeId'
    Identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    Target = '$target'
    StartedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    ListPass = `$false
    WritePass = `$false
    ReadPass = `$false
    DeletePass = `$false
    Sha256 = ''
    Error = ''
}
try {
    `$null = Get-ChildItem -LiteralPath '$NasRoot' -Force -ErrorAction Stop
    `$result.ListPass = `$true
    `$bytes = New-Object byte[] 4096
    `$random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { `$random.GetBytes(`$bytes) } finally { `$random.Dispose() }
    `$sha = [Security.Cryptography.SHA256]::Create()
    try { `$expected = ([BitConverter]::ToString(`$sha.ComputeHash(`$bytes))).Replace('-','').ToLowerInvariant() }
    finally { `$sha.Dispose() }
    [IO.File]::WriteAllBytes('$target', `$bytes)
    `$result.WritePass = `$true
    `$actual = (Get-FileHash -LiteralPath '$target' -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
    `$result.Sha256 = `$actual
    if (`$actual -ne `$expected) { throw 'Probe SHA256 mismatch.' }
    `$result.ReadPass = `$true
} catch {
    `$result.Error = `$_.Exception.Message
} finally {
    try {
        if (Test-Path -LiteralPath '$target') {
            Remove-Item -LiteralPath '$target' -Force -ErrorAction Stop
        }
        `$result.DeletePass = -not (Test-Path -LiteralPath '$target')
    } catch {
        if (-not `$result.Error) { `$result.Error = `$_.Exception.Message }
    }
    `$result.CompletedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    `$result | ConvertTo-Json | Set-Content -LiteralPath '$evidence' -Encoding UTF8
}
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probeScript))
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument `
    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' `
    -LogonType ServiceAccount -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
    -StartWhenAvailable

Register-ScheduledTask -TaskName $taskName -Action $action -Principal $taskPrincipal `
    -Settings $settings -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $task = Get-ScheduledTask -TaskName $taskName
    } while ($task.State -eq 'Running' -and (Get-Date) -lt $deadline)

    if ($task.State -eq 'Running') { throw 'The DEV NAS probe timed out.' }
    $info = Get-ScheduledTaskInfo -TaskName $taskName
    if (-not (Test-Path -LiteralPath $evidence -PathType Leaf)) {
        throw "DEV NAS probe evidence is missing. LastTaskResult=$($info.LastTaskResult)"
    }
    $result = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
    if ($result.Identity -ne 'NT AUTHORITY\LOCAL SERVICE') {
        throw 'The DEV NAS probe did not run as LOCAL SERVICE.'
    }
    if (Test-Path -LiteralPath $target) { throw 'The DEV NAS probe file was not removed.' }
    $result | ConvertTo-Json
    $status = if ($result.ListPass -and $result.WritePass -and $result.ReadPass -and
        $result.DeletePass) { 'DEV_NAS_ACCESS_PASS' } else { 'DEV_NAS_ACCESS_FAIL' }
    Write-Output $status
} finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $target) {
        throw 'The DEV NAS probe file cleanup failed.'
    }
    if ((Get-SingleListenerPid 5010) -ne $productionPid) {
        throw 'Production 5010 PID changed during the DEV NAS probe.'
    }
}
