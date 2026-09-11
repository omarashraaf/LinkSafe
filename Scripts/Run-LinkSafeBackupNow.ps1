[CmdletBinding()]
param()

$agentExe = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LinkSafeAgent\bin\LinkSafeClientAgent.exe'

if (-not (Test-Path $agentExe)) {
    throw "Client agent executable not found at $agentExe. Install or publish the client agent first."
}

& $agentExe --backup-now
