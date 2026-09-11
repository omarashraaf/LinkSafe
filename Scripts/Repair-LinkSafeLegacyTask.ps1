[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$UserName
)

$ErrorActionPreference = 'Stop'
$taskName = 'LinkSafe Client Agent'
$taskPath = '\'
$expectedExecutable = 'C:\ProgramData\LinkSafeAgent\bin\LinkSafeClientAgent.exe'

function Resolve-IdentitySid([string]$Identity) {
    if ([string]::IsNullOrWhiteSpace($Identity)) { throw 'The task has no user identity. Nothing was removed.' }
    if ($Identity.StartsWith('S-1-', [StringComparison]::OrdinalIgnoreCase)) {
        return ([Security.Principal.SecurityIdentifier]::new($Identity)).Value
    }
    return ([Security.Principal.NTAccount]::new($Identity)).Translate([Security.Principal.SecurityIdentifier]).Value
}

$targetSid = Resolve-IdentitySid $UserName
$tasks = @(Get-ScheduledTask -TaskPath $taskPath | Where-Object TaskName -EQ $taskName)
if ($tasks.Count -eq 0) {
    Write-Output 'No legacy task remains. Open the updated LinkSafe client as the normal user and click Enable agent.'
    return
}
if ($tasks.Count -ne 1) { throw 'Task selection is ambiguous. Nothing was removed.' }

[xml]$definition = Export-ScheduledTask -TaskName $taskName -TaskPath $taskPath
if ((Resolve-IdentitySid $definition.Task.Principals.Principal.UserId) -ne $targetSid) {
    throw 'The legacy task belongs to a different account. Nothing was removed.'
}
$actions = @($definition.Task.Actions.ChildNodes | Where-Object NodeType -EQ 'Element')
if ($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or
    -not [string]::Equals(([string]$actions[0].Command).Trim('"'), $expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The legacy task does not target the recognized old LinkSafe agent. Nothing was removed.'
}

Write-Output "Verified legacy task for $UserName ($targetSid): $expectedExecutable"
if (-not $PSCmdlet.ShouldProcess("$taskPath$taskName for $UserName", 'Stop and remove only the verified legacy LinkSafe startup task')) { return }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as administrator and run this same command again. No permissions or files were changed.'
    }
} finally { $identity.Dispose() }

Stop-ScheduledTask -TaskName $taskName -TaskPath $taskPath
Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Confirm:$false
if (@(Get-ScheduledTask -TaskPath $taskPath | Where-Object TaskName -EQ $taskName).Count -ne 0) {
    throw 'Windows still reports the legacy task. Repair has not completed.'
}
Write-Output 'Legacy task removed. Config, logs, queued packages, server backups and all other tasks were kept.'
Write-Output 'Close this administrator window. Open client setup 1.0.15 or later as the normal user and click Enable agent.'
