[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "LinkSafe\LinkSafeClient\LinkSafeClientAgent\LinkSafeClientAgent.csproj"
$programData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LinkSafeAgent'
$publishDir = Join-Path $programData 'bin'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "LinkSafe Client Agent ($sid)"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $programData | Out-Null

dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed. Startup was not changed.' }

$exePath = Join-Path $publishDir "LinkSafeClientAgent.exe"
if (-not (Test-Path $exePath)) {
    throw "Published client agent executable not found at $exePath"
}

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Resolve-TaskUserSid([string]$Identity) {
    if ([string]::IsNullOrWhiteSpace($Identity)) { return $null }
    if ($Identity.StartsWith('S-1-', [StringComparison]::OrdinalIgnoreCase)) { return $Identity }
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($Identity -eq $current.Name) { return $current.User.Value }
        return ([Security.Principal.NTAccount]::new($Identity).Translate([Security.Principal.SecurityIdentifier])).Value
    } finally { $current.Dispose() }
}
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    [xml]$oldXml = Export-ScheduledTask -TaskName $taskName
    if ((Resolve-TaskUserSid $oldXml.Task.Principals.Principal.UserId) -ne $sid -or $oldXml.Task.Actions.Exec.Command.Trim('"') -ne $exePath) {
        throw 'Existing task belongs to another user or installation. It was not replaced; use the packaged client to repair its own installation.'
    }
}
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $publishDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $sid
$trigger.Delay = 'PT15S'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -Hidden -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
[xml]$verified = Export-ScheduledTask -TaskName $taskName
if ((Resolve-TaskUserSid $verified.Task.Principals.Principal.UserId) -ne $sid -or (Resolve-TaskUserSid $verified.Task.Triggers.LogonTrigger.UserId) -ne $sid -or $verified.Task.Actions.Exec.Command -ne $exePath -or $verified.Task.Settings.DisallowStartIfOnBatteries -ne 'false' -or $verified.Task.Settings.ExecutionTimeLimit -ne 'PT0S') {
    throw 'Startup task did not pass read-back verification.'
}
Start-ScheduledTask -TaskName $taskName

Write-Host "LinkSafe Client Agent installed and scheduled at user logon."
