[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "LinkSafe Client Agent ($sid)"

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    $task = Get-ScheduledTask -TaskName $taskName
    $owner = $task.Principal.UserId
    if ($owner -notlike 'S-1-*') { $owner = ([Security.Principal.NTAccount]::new($owner)).Translate([Security.Principal.SecurityIdentifier]).Value }
    if ($owner -ne $sid) { throw 'Task ownership does not match this user; nothing was removed.' }
    Stop-ScheduledTask -TaskName $taskName
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Write-Host "LinkSafe Client Agent scheduled task removed. Config, logs, and queue were preserved."
