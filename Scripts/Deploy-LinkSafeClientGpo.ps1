# Run through User Configuration > Windows Settings > Scripts > Logon (PowerShell).
# Put this script in the GPO's SYSVOL script folder; keep large binaries on a separate read-only share.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$ServerIp,
    [ValidateRange(1,65535)][int]$Port = 5055,
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$DeploymentId = 'default-folders-fixed-drives-v1',
    [ValidateRange(0,600)][int]$MaxStartDelaySeconds = 180
)
$ErrorActionPreference = 'Stop'
$log = $null
$gate = $null

function Assert-NoLinks([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            if ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Deployment path contains a link/reparse point: $itemPath"
            }
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}
function Test-Hash([string]$Path, [string]$Expected) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected)
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($identity.IsSystem -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or
        $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Use a non-elevated USER logon GPO, not a computer startup script or Run as SYSTEM.'
    }
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($ServerIp, [ref]$ip) -or [Net.IPAddress]::IsLoopback($ip) -or
        $ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any) -or $ip.Equals([Net.IPAddress]::Broadcast)) {
        throw 'Enter a reachable server IP only, not a URL or localhost.'
    }
    if (-not [IO.Path]::IsPathRooted($PackagePath) -or $PackagePath.Contains('"')) { throw 'PackagePath must be an absolute local or UNC folder.' }
    $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LinkSafeDeploymentCache'
    Assert-NoLinks $cache
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($identity.User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $cache -AclObject $acl
    $log = Join-Path $cache 'deployment.log'
    Assert-NoLinks $log
    $lock = Join-Path $cache '.deployment.lock'
    Assert-NoLinks $lock
    $gate = [IO.File]::Open($lock, 'OpenOrCreate', 'ReadWrite', 'None')
    if ($MaxStartDelaySeconds -gt 0) { Start-Sleep -Seconds (Get-Random -Minimum 0 -Maximum ($MaxStartDelaySeconds + 1)) }
    $manifestFile = Join-Path $PackagePath 'package.json'
    if ((Get-Item -LiteralPath $manifestFile).Length -gt 16384) { throw 'Invalid deployment manifest size.' }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1) { throw 'Unsupported deployment manifest.' }
    $names = @('LinkSafeClientSetup.exe', 'LinkSafeClientAgent.exe')
    foreach ($name in $names) {
        $hash = $manifest.Hashes.$name
        if ($hash -isnot [string] -or $hash -notmatch '^[A-Fa-f0-9]{64}$') { throw "Invalid hash for $name" }
        $target = Join-Path $cache $name
        Assert-NoLinks $target
        if (-not (Test-Hash $target $hash)) {
            $temporary = Join-Path $cache ([guid]::NewGuid().ToString('N') + '.download')
            try {
                Copy-Item -LiteralPath (Join-Path $PackagePath $name) -Destination $temporary
                if (-not (Test-Hash $temporary $hash)) { throw "SHA-256 mismatch for $name. Nothing will execute." }
                # Compatible with Windows PowerShell 5.1; null backup path is not an empty string.
                if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [System.Management.Automation.Language.NullString]::Value) }
                else { [IO.File]::Move($temporary, $target) }
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        }
    }
    $setup = Join-Path $cache 'LinkSafeClientSetup.exe'
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
    if ([version]$info.FileVersion -lt [version]'1.0.17' -or $info.OriginalFilename -notin @('LinkSafeClientSetup.dll','LinkSafeClientSetup.exe')) {
        throw 'This package does not support unattended GPO installation. Build the GPO kit; do not use an old Downloads EXE.'
    }
    $agentHash = $manifest.Hashes.'LinkSafeClientAgent.exe'
    $arguments = '--gpo-install --server-ip {0} --port {1} --package-root "{2}" --agent-sha256 {3} --deployment-id {4}' -f $ServerIp,$Port,$cache,$agentHash,$DeploymentId
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        # Do not kill an installer in the middle of a verified write. A later logon can retry.
        throw 'Deployment is still running. Inspect gpo-install.log; it was not forcibly terminated.'
    }
    if ($process.ExitCode -ne 0) { throw "Client deployment failed (exit $($process.ExitCode)); see %LOCALAPPDATA%\LinkSafeAgent\Logs\gpo-install.log." }
    Add-Content -LiteralPath $log -Value ("{0:o} Deployment applied/already applied for {1}. Backup completion is reported by the agent, not this script." -f (Get-Date),$identity.Name)
    exit 0
} catch {
    if ($log) { Add-Content -LiteralPath $log -Value ("{0:o} FAILED: {1}" -f (Get-Date),$_.Exception.Message) -ErrorAction SilentlyContinue }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($gate) { $gate.Dispose() }
    if ($identity) { $identity.Dispose() }
}
