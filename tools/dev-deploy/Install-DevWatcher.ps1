$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\CSIT-DEV'
$branch = 'feature/km-stage-integration'
$taskName = 'CSIT DEV Ready Watcher'
$productionBefore = @(Get-NetTCPConnection -State Listen -LocalPort 5010 |
    Select-Object -ExpandProperty OwningProcess -Unique)
if ($productionBefore.Count -ne 1) { throw 'Production PID guard failed.' }
if ((git branch --show-current).Trim() -ne $branch) { throw 'Wrong source branch for DEV watcher installation.' }
$baselineSha = (git rev-parse HEAD).Trim()
if ($baselineSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record the current DEV baseline.' }

$directories = @($root, (Join-Path $root 'automation'), (Join-Path $root 'secrets'),
    (Join-Path $root 'releases'), (Join-Path $root 'work'), (Join-Path $root 'logs'),
    (Join-Path $root 'state'))
foreach ($path in $directories) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
& icacls.exe $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'DEV deployment root ACL failed.' }
foreach ($restricted in @('automation', 'secrets')) {
    & icacls.exe (Join-Path $root $restricted) /inheritance:r /grant:r `
        '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'DEV restricted directory ACL failed.' }
}

$userSecrets = Join-Path $env:APPDATA `
    'Microsoft\UserSecrets\c75615ee-27bd-4780-b3ac-aa6bef5b6fe7\secrets.json'
$sourceSettings = Join-Path (Get-Location).Path 'appsettings.json'
if (-not (Test-Path -LiteralPath $userSecrets) -or -not (Test-Path -LiteralPath $sourceSettings)) {
    throw 'Existing CSIT DEV runtime configuration is unavailable.'
}
$targetSecrets = Join-Path $root 'secrets\runtime.json'
$targetSettings = Join-Path $root 'secrets\base-appsettings.json'
Copy-Item -LiteralPath $userSecrets -Destination $targetSecrets -Force
Copy-Item -LiteralPath $sourceSettings -Destination $targetSettings -Force
foreach ($file in @($targetSecrets, $targetSettings)) {
    & icacls.exe $file /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'DEV configuration file ACL failed.' }
}
$secrets = Get-Content -LiteralPath $targetSecrets -Raw | ConvertFrom-Json
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
$connection.set_ConnectionString([string] $secrets.PSObject.Properties['ConnectionStrings:Postgres'].Value)
if ([string] $connection['Database'] -ne 'sit_department_system_dev' -or
    [string] $connection['Username'] -ne 'csit_runtime_dev') {
    throw 'The copied runtime credential does not target the DEV database and role.'
}

foreach ($thumbprint in @('46BFDA36B073576B3116CB5E52D7ABE1FC595F2D',
        'D4E5BA5E6D6252575BB8D0ADD3F2A98B279C503B')) {
    $certificate = Get-ChildItem "Cert:\LocalMachine\My\$thumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) {
        throw 'A DEV TLS certificate is unavailable.'
    }
    $keyName = $certificate.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
    $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $keyName)
    if (-not (Test-Path -LiteralPath $keyFile)) { throw 'A DEV TLS certificate key is unavailable.' }
    & icacls.exe $keyFile /grant '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant DEV certificate read access.' }
}

$scriptRoot = $PSScriptRoot
foreach ($name in @('Watch-DevReady.ps1', 'Deploy-DevReady.ps1')) {
    $target = Join-Path $root ('automation\' + $name)
    Copy-Item -LiteralPath (Join-Path $scriptRoot $name) -Destination $target -Force
    & icacls.exe $target /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-19:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'DEV automation script ACL failed.' }
}
$baselineFile = Join-Path $root 'state\baseline.json'
if (-not (Test-Path -LiteralPath $baselineFile)) {
    @{Sha=$baselineSha; RecordedAtUtc=(Get-Date).ToUniversalTime().ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath $baselineFile -Encoding UTF8
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument `
    ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
        (Join-Path $root 'automation\Watch-DevReady.ps1') + '"')
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' `
    -LogonType ServiceAccount -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
    -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
if (@(Get-NetTCPConnection -State Listen -LocalPort 5010 |
        Select-Object -ExpandProperty OwningProcess -Unique)[0] -ne $productionBefore[0]) {
    throw 'Production PID changed during DEV watcher installation.'
}
Write-Output ('DEV_WATCHER_INSTALLED task=' + $taskName + ' principal=LOCAL SERVICE baseline=' + $baselineSha)
