[CmdletBinding(DefaultParameterSetName = 'Interactive')]
param(
    [Parameter(ParameterSetName = 'Interactive')]
    [switch] $Remove,
    [Parameter(Mandatory = $true, ParameterSetName = 'Worker')]
    [switch] $CredentialWorker,
    [Parameter(Mandatory = $true, ParameterSetName = 'Worker')]
    [ValidatePattern('^csit-dev-nas-[0-9a-f]{32}$')]
    [string] $PipeName,
    [Parameter(Mandatory = $true, ParameterSetName = 'Worker')]
    [ValidateSet('Write','Delete')]
    [string] $Operation
)

$ErrorActionPreference = 'Stop'
$targetName = 'AI-NAS'
$nasRoot = '\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV'
$localService = 'NT AUTHORITY\LOCAL SERVICE'
$localServiceSid = 'S-1-5-19'
$taskName = $null
$server = $null
$productionPid = 0

function Add-CredentialNativeType {
    if ('CsitDevCredentialNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class CsitDevCredentialNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern bool CredDelete(string target, int type, int flags);
}
'@
}

function Write-DomainCredential([string] $UserName, [string] $Password) {
    Add-CredentialNativeType
    $blob = [Runtime.InteropServices.Marshal]::StringToCoTaskMemUni($Password)
    try {
        $credential = [CsitDevCredentialNative+Credential]::new()
        $credential.Type = 2 # CRED_TYPE_DOMAIN_PASSWORD
        $credential.TargetName = $targetName
        $credential.CredentialBlobSize = [Text.Encoding]::Unicode.GetByteCount($Password)
        $credential.CredentialBlob = $blob
        $credential.Persist = 2 # CRED_PERSIST_LOCAL_MACHINE
        $credential.UserName = $UserName
        if (-not [CsitDevCredentialNative]::CredWrite([ref]$credential, 0)) {
            $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Credential Manager rejected the DEV NAS credential. Win32=$code"
        }
    } finally {
        if ($blob -ne [IntPtr]::Zero) {
            $zero = New-Object byte[] ([Text.Encoding]::Unicode.GetByteCount($Password))
            [Runtime.InteropServices.Marshal]::Copy($zero, 0, $blob, $zero.Length)
            [Runtime.InteropServices.Marshal]::ZeroFreeCoTaskMemUnicode($blob)
        }
    }
}

function Remove-DomainCredential {
    Add-CredentialNativeType
    if (-not [CsitDevCredentialNative]::CredDelete($targetName, 2, 0)) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($code -ne 1168) { throw "Credential Manager delete failed. Win32=$code" }
    }
}

function Get-SingleListenerPid([int] $Port) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -ne 1) { throw "Port $Port must have exactly one listener." }
    return [int]$listeners[0]
}

if ($PSCmdlet.ParameterSetName -eq 'Worker') {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().Name -ne $localService) {
        throw 'The credential worker must run as LOCAL SERVICE.'
    }
    $client = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName,
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None,
        [Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $client.Connect(30000)
        $reader = [IO.StreamReader]::new($client, [Text.Encoding]::UTF8, $false, 1024, $true)
        $writer = [IO.StreamWriter]::new($client, [Text.UTF8Encoding]::new($false), 1024, $true)
        $writer.AutoFlush = $true
        try {
            if ($Operation -eq 'Write') {
                $payload = $reader.ReadLine() | ConvertFrom-Json
                $userName = [string]$payload.UserName
                $password = [string]$payload.Password
                if ([string]::IsNullOrWhiteSpace($userName) -or
                    [string]::IsNullOrEmpty($password)) {
                    throw 'The in-memory credential payload is invalid.'
                }
                Write-DomainCredential $userName $password
                $password = $null
                $payload = $null
            } else {
                Remove-DomainCredential
            }
            $writer.WriteLine('{"Status":"PASS","Identity":"LOCAL SERVICE"}')
        } catch {
            $writer.WriteLine('{"Status":"FAIL"}')
            throw
        } finally {
            $reader.Dispose()
            $writer.Dispose()
        }
    } finally {
        $client.Dispose()
    }
    exit 0
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Windows PowerShell console on the DEV host.'
}
$productionPid = Get-SingleListenerPid 5010
$devPid = Get-SingleListenerPid 7444
$devProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$devPid"
$devOwner = Invoke-CimMethod -InputObject $devProcess -MethodName GetOwner
if (($devOwner.Domain + '\' + $devOwner.User) -ne $localService) {
    throw 'The current DEV runtime is not running as LOCAL SERVICE.'
}

$pipeName = 'csit-dev-nas-' + [Guid]::NewGuid().ToString('N')
$pipeSecurity = [IO.Pipes.PipeSecurity]::new()
$pipeSecurity.SetAccessRuleProtection($true, $false)
foreach ($sidValue in @($identity.User.Value, $localServiceSid)) {
    $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
    $rule = [IO.Pipes.PipeAccessRule]::new($sid,
        [IO.Pipes.PipeAccessRights]::ReadWrite,
        [Security.AccessControl.AccessControlType]::Allow)
    $pipeSecurity.AddAccessRule($rule)
}
$server = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut,
    1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous,
    4096, 4096, $pipeSecurity)

$operation = if ($Remove) { 'Delete' } else { 'Write' }
$taskName = 'CSIT DEV NAS Credential ' + [Guid]::NewGuid().ToString('N')
$scriptPath = [IO.Path]::GetFullPath($PSCommandPath)
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath +
    '" -CredentialWorker -PipeName ' + $pipeName + ' -Operation ' + $operation
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $localService -LogonType ServiceAccount `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

$securePassword = $null
$plainPassword = $null
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $taskPrincipal `
        -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $wait = $server.BeginWaitForConnection($null, $null)
    if (-not $wait.AsyncWaitHandle.WaitOne(30000)) {
        throw 'Timed out waiting for the LOCAL SERVICE credential worker.'
    }
    $server.EndWaitForConnection($wait)
    $reader = [IO.StreamReader]::new($server, [Text.Encoding]::UTF8, $false, 1024, $true)
    $writer = [IO.StreamWriter]::new($server, [Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.AutoFlush = $true
    try {
        if (-not $Remove) {
            $confirmation = Read-Host 'Confirm this is a non-admin NAS account limited to the DEV root (type CSIT-DEV)'
            if ($confirmation -cne 'CSIT-DEV') {
                throw 'Credential installation cancelled: least-privilege confirmation was not supplied.'
            }
            $userName = Read-Host 'NAS user (use AI-NAS\account unless your NAS requires another form)'
            if ([string]::IsNullOrWhiteSpace($userName)) { throw 'NAS user is required.' }
            $securePassword = Read-Host 'NAS password (input is hidden and never stored in a file)' -AsSecureString
            $plainPassword = [Net.NetworkCredential]::new('', $securePassword).Password
            $payload = @{ UserName=$userName; Password=$plainPassword } | ConvertTo-Json -Compress
            $writer.WriteLine($payload)
            $payload = $null
            $plainPassword = $null
        }
        $responseLine = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseLine)) {
            throw 'The LOCAL SERVICE credential worker returned no result.'
        }
        $response = $responseLine | ConvertFrom-Json
        if ([string]$response.Status -ne 'PASS') {
            throw 'The LOCAL SERVICE credential worker failed; no credential was accepted.'
        }
    } finally {
        $reader.Dispose()
        $writer.Dispose()
    }
    if ($Remove) {
        Write-Output "DEV_NAS_CREDENTIAL_REMOVED target=$targetName identity=$localService"
    } else {
        Write-Output "DEV_NAS_CREDENTIAL_STORED target=$targetName identity=$localService"
        Write-Output "Next: run Test-DevNasAccess.ps1; access is not proven until that probe passes."
    }
} finally {
    $plainPassword = $null
    $securePassword = $null
    if ($server) { $server.Dispose() }
    if ($taskName) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    if ((Get-SingleListenerPid 5010) -ne $productionPid) {
        throw 'Production 5010 PID changed during DEV NAS credential maintenance.'
    }
}
