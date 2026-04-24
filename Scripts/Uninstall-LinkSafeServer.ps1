[CmdletBinding()]
param(
    [switch]$RemoveConfig
)

$ErrorActionPreference = "Stop"
$serviceName = "LinkSafeServer"
$programData = "C:\ProgramData\LinkSafeServer"

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}

if ($RemoveConfig -and (Test-Path $programData)) {
    Remove-Item -LiteralPath $programData -Recurse -Force
}

Write-Host "LinkSafe Server removed. Config, logs, and backups were kept unless -RemoveConfig was used."
