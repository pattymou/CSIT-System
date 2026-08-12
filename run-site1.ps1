$ErrorActionPreference = "Stop"

$sitePath = "D:\CSIT_Publish\SIT.DepartmentSystem.Web"
$dllName = "SIT.DepartmentSystem.Web.dll"
$url = "http://0.0.0.0:5010"

Write-Host "Stopping old dotnet process on port 5010..."

$connections = Get-NetTCPConnection -LocalPort 5010 -ErrorAction SilentlyContinue

foreach ($conn in $connections)
{
    if ($conn.OwningProcess -gt 0)
    {
        Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
    }
}

Set-Location $sitePath

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = $url

Write-Host "Starting CSIT System at $url ..."

Start-Process "dotnet" `
    -ArgumentList "$dllName --urls $url" `
    -WorkingDirectory $sitePath `
    -WindowStyle Hidden

Start-Sleep -Seconds 3

Write-Host ""
Write-Host "Listening Ports:"
netstat -ano | findstr ":5010"