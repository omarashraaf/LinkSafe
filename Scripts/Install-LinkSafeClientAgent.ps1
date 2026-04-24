[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "LinkSafe\LinkSafeClient\LinkSafeClientAgent\LinkSafeClientAgent.csproj"
$publishDir = Join-Path $root "artifacts\LinkSafeClientAgent"
$programData = "C:\ProgramData\LinkSafeAgent"
$taskName = "LinkSafe Client Agent"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $programData | Out-Null

dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publishDir

$exePath = Join-Path $publishDir "LinkSafeClientAgent.exe"
if (-not (Test-Path $exePath)) {
    throw "Published client agent executable not found at $exePath"
}

$action = New-ScheduledTaskAction -Execute $exePath
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "LinkSafe Client Agent installed and scheduled at user logon."
