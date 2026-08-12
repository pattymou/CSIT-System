$ErrorActionPreference = "Stop"

$sitePath = "D:\CSIT_Publish\SIT.DepartmentSystem.Web"
$dllName = "SIT.DepartmentSystem.Web.dll"
$url = "http://127.0.0.1:5010"

Set-Location $sitePath

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = $url

Write-Host "====================================="
Write-Host "Starting CSIT System"
Write-Host "Path: $sitePath"
Write-Host "DLL : $dllName"
Write-Host "URL : $url"
Write-Host "ENV : $env:ASPNETCORE_ENVIRONMENT"
Write-Host "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "====================================="

dotnet $dllName --urls $url