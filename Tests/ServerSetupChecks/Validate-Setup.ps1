$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$helper = Join-Path $root 'Scripts\ServerSetup\Manage-LinkSafeServer.ps1'
$errors = $null
$tokens = $null
$null = [Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
. $helper
$checks = 1
foreach ($path in @('..\data','\\host\share','C:\','C:\Windows\backup','C:\Program Files\backup','C:\ProgramData\backup','C:\Users\backup','C:\Backups\..\data','C:\Backups:stream','C:\Bad.\data','C:\Bad*\data')) {
    $rejected = $false
    try { $null = Assert-BackupPath $path -Fresh } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted unsafe backup location: $path" }
    $checks++
}
if (-not (Test-Within 'C:\Demo\file' 'C:\Demo')) { throw 'Child path rejected.' }
if (Test-Within 'C:\DemoElsewhere\file' 'C:\Demo') { throw 'Sibling prefix accepted.' }
$checks += 2
Write-Output "PASS: $checks PowerShell setup validation checks. Installation code was not executed."
