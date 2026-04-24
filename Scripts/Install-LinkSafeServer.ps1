[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [int]$Port = 5055,
    [switch]$OpenFirewall
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "LinkSafe\LinkSafeServer\LinkSafeServer.csproj"
$publishDir = Join-Path $root "artifacts\LinkSafeServer"
$serviceName = "LinkSafeServer"
$programData = "C:\ProgramData\LinkSafeServer"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $programData | Out-Null

dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publishDir

$exePath = Join-Path $publishDir "LinkSafeServer.exe"
if (-not (Test-Path $exePath)) {
    throw "Published server executable not found at $exePath"
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $serviceName binPath= "`"$exePath`"" start= auto | Out-Null
sc.exe description $serviceName "LinkSafe secure backup server" | Out-Null
Start-Service -Name $serviceName

if ($OpenFirewall) {
    New-NetFirewallRule -DisplayName "LinkSafe Server $Port" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -ErrorAction SilentlyContinue | Out-Null
}

Write-Host "LinkSafe Server installed."
Write-Host "URL: http://$env:COMPUTERNAME:$Port"
