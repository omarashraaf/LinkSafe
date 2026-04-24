[CmdletBinding()]
param()

$root = Split-Path -Parent $PSScriptRoot
$agentExe = Join-Path $root "artifacts\LinkSafeClientAgent\LinkSafeClientAgent.exe"

if (-not (Test-Path $agentExe)) {
    throw "Client agent executable not found at $agentExe. Install or publish the client agent first."
}

& $agentExe --backup-now
