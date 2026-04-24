[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "LinkSafe Client Agent"

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Write-Host "LinkSafe Client Agent scheduled task removed. Config, logs, and queue were preserved."
