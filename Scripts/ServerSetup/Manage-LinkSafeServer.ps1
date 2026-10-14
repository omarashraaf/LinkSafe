[CmdletBinding()]
param(
    [ValidateSet('Install','EnableLan','Uninstall','OpenUi','Validate')][string]$Mode = 'Validate',
    [string]$PayloadRoot,
    [string]$BackupRoot = 'C:\LinkSafeBackups',
    [ValidateRange(1024,65535)][int]$Port = 5055
)
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramFiles 'LinkSafe Server'
$dataRoot = 'C:\ProgramData\LinkSafeServer'
$configPath = Join-Path $dataRoot 'server-config.json'
$markerPath = Join-Path $installRoot '.linksafe-install.json'
$serverPath = Join-Path $installRoot 'Server'
$exe = Join-Path $serverPath 'LinkSafeServer.exe'
$serviceName = 'LinkSafeServer'
$ruleName = 'LinkSafe-Server-LAN'
$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LinkSafeServer'

function Assert-NoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links or junctions are not allowed in setup paths: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Test-Within([string]$Path, [string]$Root) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $r = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $p.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Assert-BackupPath([string]$Path, [switch]$Fresh) {
    if ($Path -notmatch '^[A-Za-z]:\\' -or $Path -match '["<>|?*\x00-\x1f]' -or $Path.Substring(2).Contains(':') -or $Path -match '(^|\\)\.\.?($|\\)' -or $Path -match '[ .](\\|$)') {
        throw 'Choose an absolute local folder, for example D:\LinkSafeBackups. Network paths and relative paths are not allowed.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le 2) { throw 'The backup folder cannot be a drive root.' }
    foreach ($part in $full.Substring(3).Split('\')) {
        if ($part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw 'Backup path contains a reserved Windows name.' }
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))
    if (-not $drive.IsReady -or $drive.DriveFormat -ne 'NTFS' -or $drive.DriveType -ne 'Fixed') { throw 'Choose a ready, fixed NTFS drive for server backups.' }
    foreach ($protected in @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, 'C:\ProgramData', (Join-Path $env:SystemDrive 'Users'))) {
        if ($protected -and ((Test-Within $full $protected) -or (Test-Within $protected $full))) { throw "Choose a dedicated backup folder outside $protected." }
    }
    Assert-NoLinks $full
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw 'Backup location is not a directory.' }
        if ($Fresh -and @(Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1).Count) { throw 'For first installation, choose a new or empty backup folder. Existing data will not be modified.' }
    }
    return $full
}
function Protect-Directory([string]$Path) {
    Assert-NoLinks $Path
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sidText in @('S-1-5-18','S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidText)
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Resolve-LocalSharePath([string]$Path, [string[]]$LocalNames, [object[]]$Shares) {
    if ($Path -notmatch '^\\\\([^\\]+)\\([^\\]+)(\\.*)?$') { throw 'The saved network backup path is invalid.' }
    $hostName = $Matches[1]
    $shareName = $Matches[2]
    $suffix = $Matches[3]
    if ($LocalNames -notcontains $hostName) {
        throw 'The saved backup share is not on this server. Setup requires local NTFS storage; it will not move or replace remote backups.'
    }
    $share = @($Shares | Where-Object { $_.Name -eq $shareName -and $_.ScopeName -eq '*' -and -not $_.Scoped })
    if ($share.Count -ne 1 -or $share[0].Path -notmatch '^[A-Za-z]:\\') {
        throw 'Cannot verify the saved share as a single local folder. Check its local Path with Get-SmbShare on this server.'
    }
    # Keep the suffix unnormalized so Assert-BackupPath can reject traversal and aliases.
    return $share[0].Path.TrimEnd('\') + $suffix
}
function Get-UpgradeBackupPath([string]$Path) {
    if (-not $Path.StartsWith('\\')) { return Assert-BackupPath $Path }
    $names = @('localhost', '127.0.0.1', $env:COMPUTERNAME)
    $network = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
    if ($network.DomainName) { $names += $network.HostName + '.' + $network.DomainName }
    $names += @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Select-Object -ExpandProperty IPAddress)
    $local = Resolve-LocalSharePath $Path $names @(Get-SmbShare -ErrorAction Stop)
    $local = Assert-BackupPath $local
    if (-not (Test-Path -LiteralPath $local -PathType Container)) { throw 'The local folder behind the saved share is missing. Setup will not create a replacement backup location.' }
    return $local
}
function Convert-StorageConfig($Config, [string]$LocalRoot) {
    $copy = $Config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.BackupRoot = $LocalRoot
    foreach ($name in @('Incoming','Quarantine','Versions','Logs')) {
        $copy | Add-Member -NotePropertyName ($name + 'Root') -NotePropertyValue (Join-Path $LocalRoot ('_' + $name)) -Force
    }
    return $copy
}
function Convert-ClientDestinations([object[]]$Clients, [string]$OldRoot, [string]$LocalRoot) {
    foreach ($client in $Clients) {
        $copy = $client | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $prefix = $OldRoot.TrimEnd('\') + '\'
        $destination = [string]$copy.BackupDestinationPath
        if ($destination.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $mapped = $LocalRoot.TrimEnd('\') + '\' + $destination.Substring($prefix.Length)
            try { $copy.BackupDestinationPath = Assert-BackupPath $mapped }
            catch { throw "Cannot update registered client destination '$destination' to '$mapped': $($_.Exception.Message)" }
        }
        $copy
    }
}
function Read-ClientRegistry([string]$Path) {
    Assert-NoLinks $Path
    $json = Get-Content -LiteralPath $Path -Raw
    if (-not $json.TrimStart().StartsWith('[')) { throw 'The registered clients file must contain a JSON array. It has not been changed.' }
    # PS 5.1 emits a JSON array as ONE pipeline object. Evaluate it before wrapping
    # in @() so each record is enumerated, just as it is on PowerShell 7.
    $clients = @(($json | ConvertFrom-Json))
    foreach ($client in $clients) {
        if ($null -eq $client -or $client -is [array] -or $client.BackupDestinationPath -isnot [string]) {
            throw 'A registered client record has an invalid backup destination. The registry has not been changed.'
        }
        $client
    }
}
function Save-Json([string]$Path, $Value) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    ConvertTo-Json -InputObject $Value -Depth 20 | Set-Content -LiteralPath $temp -Encoding UTF8
    # PowerShell converts $null to an empty string for this .NET string parameter.
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp,$Path,[System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp,$Path) }
}
function Get-OwnedService {
    $service = Get-CimInstance Win32_Service -Filter "Name='LinkSafeServer'" -ErrorAction Stop
    if ($service -and $service.PathName -ne ('"' + $exe + '"')) {
        throw 'A different LinkSafeServer installation already exists. Setup will not stop or replace it. Use its original uninstaller or a clean device.'
    }
    return $service
}
function Assert-Ownership {
    Assert-NoLinks $installRoot
    Assert-NoLinks $dataRoot
    $exists = Test-Path -LiteralPath $markerPath
    if ($exists) {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.Product -ne 'LinkSafeServerSetup' -or $marker.InstallRoot -ne $installRoot) { throw 'Installation ownership marker is invalid.' }
    } elseif ((Test-Path -LiteralPath $configPath) -or ((Test-Path -LiteralPath $installRoot) -and @(Get-ChildItem -LiteralPath $installRoot -Force | Select-Object -First 1).Count) -or ((Test-Path -LiteralPath $dataRoot) -and @(Get-ChildItem -LiteralPath $dataRoot -Force | Select-Object -First 1).Count)) {
        throw 'Existing LinkSafe files were found without this installer ownership marker. They have been left untouched. Use a clean device or migrate the installation manually.'
    }
    $null = Get-OwnedService
    return $exists
}
function Wait-Healthy([int]$ListenPort) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $result = Invoke-RestMethod -Uri "http://127.0.0.1:$ListenPort/api/health" -TimeoutSec 2
            if ($result.status -eq 'ok') { return }
        } catch { }
        Start-Sleep -Seconds 1
    }
    throw 'Server did not become healthy. Inspect C:\ProgramData\LinkSafeServer\Logs.'
}
function Stop-OwnedService {
    $owned = Get-OwnedService
    if ($owned) {
        Stop-Service -Name $serviceName -ErrorAction Stop
        (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    }
}
function Remove-InstallChild([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -eq $installRoot -or -not (Test-Within $full $installRoot)) { throw 'Unsafe install cleanup path.' }
    Assert-NoLinks $full
    if (Test-Path -LiteralPath $full) {
        foreach ($item in Get-ChildItem -LiteralPath $full -Force -Recurse) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Cleanup refused because installation contains a link.' }
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

# Dot-sourcing is a side-effect-free way for the validation tests to load these functions.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($Mode -eq 'OpenUi') {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Start-Process "http://127.0.0.1:$(([uri]$config.ServerUrl).Port)/"
    exit 0
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run LinkSafe Server Setup as administrator.'
}
$mutex = [Threading.Mutex]::new($false,'Global\LinkSafeServerSetup')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another LinkSafe setup is already running.' }
    $ownedInstall = Assert-Ownership
    if ($Mode -eq 'Validate' -or $Mode -eq 'Install') {
        $oldService = Get-OwnedService
        $config = $null
        if (Test-Path -LiteralPath $configPath) {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $BackupRoot = Get-UpgradeBackupPath $config.BackupRoot
            if ($BackupRoot -ne $config.BackupRoot) {
                Write-Output "Verified same-server share: $($config.BackupRoot) -> $BackupRoot. Update will use the existing local folder; no backup files will be moved."
            }
            $uri = [uri]$config.ServerUrl
            if ($uri.Scheme -ne 'http') { throw 'This setup manages HTTP installations only. HTTPS installations require manual upgrade.' }
            $Port = $uri.Port
        } else { $BackupRoot = Assert-BackupPath $BackupRoot -Fresh }
        $clientsPath = Join-Path $dataRoot 'registered-clients.json'
        if ($config -and $config.BackupRoot -ne $BackupRoot -and (Test-Path -LiteralPath $clientsPath)) {
            $previewClients = @(Read-ClientRegistry $clientsPath)
            $null = @(Convert-ClientDestinations $previewClients $config.BackupRoot $BackupRoot)
            Write-Output "Client destination checks passed: $($previewClients.Count) records."
        }
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
        if (@($listeners | Where-Object { -not $oldService -or $_.OwningProcess -ne $oldService.ProcessId }).Count) { throw "Port $Port is already in use by another application. Choose another port." }
        Write-Output "Destination checks passed. Backup folder: $BackupRoot; port: $Port."
        if ($Mode -eq 'Validate') { exit 0 }
        if (-not $PayloadRoot -or -not (Test-Path -LiteralPath (Join-Path $PayloadRoot 'Server\LinkSafeServer.exe'))) { throw 'Server payload was not found.' }
        Assert-NoLinks $PayloadRoot
        foreach ($item in Get-ChildItem -LiteralPath $PayloadRoot -Force -Recurse) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload contains a link.' }
        }
        if (-not $ownedInstall) { Protect-Directory $installRoot; Protect-Directory $dataRoot }
        $toolsDir = Join-Path $installRoot 'SetupTools'
        New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
        Save-Json $markerPath @{ Product='LinkSafeServerSetup'; InstallRoot=$installRoot; Version='1.0.21' }
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $toolsDir 'Manage-LinkSafeServer.ps1') -Force
        if (-not $config) {
            Protect-Directory $BackupRoot
            foreach ($name in @('_Incoming','_Quarantine','_Versions','_Logs')) { New-Item -ItemType Directory -Path (Join-Path $BackupRoot $name) -Force | Out-Null }
            $config = [ordered]@{ BackupRoot=$BackupRoot; ServerUrl="http://127.0.0.1:$Port"; AdminUsername='admin'; AdminPasswordHash=''; ServiceAccount='NT AUTHORITY\SYSTEM' }
            Save-Json $configPath $config
        }
        $next = Join-Path $installRoot ('Server.next-' + [guid]::NewGuid().ToString('N'))
        $previous = Join-Path $installRoot ('Server.previous-' + [guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath (Join-Path $PayloadRoot 'Server') -Destination $next -Recurse
        $createdService = $false
        $movedPrevious = $false
        $promoted = $false
        $configChanged = $false
        $clientsChanged = $false
        try {
            Write-Output 'Installing server binaries and Windows service...'
            Stop-OwnedService
            if ($config.BackupRoot -ne $BackupRoot) {
                # Recheck after stopping the writer; never migrate files or guess a drive letter.
                $originalConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
                if ($originalConfig.BackupRoot -ne $config.BackupRoot -or (Get-UpgradeBackupPath $originalConfig.BackupRoot) -ne $BackupRoot) {
                    throw 'Backup storage changed during setup. Retry after reviewing the current settings.'
                }
                $newConfig = Convert-StorageConfig $originalConfig $BackupRoot
                if (Test-Path -LiteralPath $clientsPath) {
                    $originalClients = @(Read-ClientRegistry $clientsPath)
                    $newClients = @(Convert-ClientDestinations $originalClients $originalConfig.BackupRoot $BackupRoot)
                    Save-Json $clientsPath $newClients
                    $clientsChanged = $true
                }
                Save-Json $configPath $newConfig
                $configChanged = $true
            }
            # Both move targets are fixed children of the verified installer-owned directory.
            Assert-NoLinks $serverPath
            if (-not (Test-Within $previous $installRoot) -or -not (Test-Within $next $installRoot)) { throw 'Unsafe staging path.' }
            if (Test-Path -LiteralPath $serverPath) { Move-Item -LiteralPath $serverPath -Destination $previous; $movedPrevious = $true }
            Move-Item -LiteralPath $next -Destination $serverPath
            $promoted = $true
            if (-not $oldService) {
                New-Service -Name $serviceName -BinaryPathName ('"' + $exe + '"') -DisplayName 'LinkSafe Server' -Description 'LinkSafe backup Web UI and API' -StartupType Automatic | Out-Null
                $createdService = $true
            }
            Set-Service -Name $serviceName -StartupType Automatic
            & sc.exe failure $serviceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
            Start-Service -Name $serviceName
            Wait-Healthy $Port
        } catch {
            $failure = $_
            Write-Output 'Service installation failed. Restoring previous server binaries; backup data is not changed.'
            Stop-OwnedService
            if ($createdService) { & sc.exe delete $serviceName | Out-Null }
            if ($promoted) { Remove-InstallChild $serverPath }
            if ($movedPrevious) { Move-Item -LiteralPath $previous -Destination $serverPath }
            if ($clientsChanged) { Save-Json $clientsPath $originalClients }
            if ($configChanged) { Save-Json $configPath $originalConfig }
            if ($oldService -and $oldService.State -eq 'Running') { Start-Service -Name $serviceName }
            throw $failure
        }
        $downloads = Join-Path $dataRoot 'Downloads'
        Assert-NoLinks $downloads
        New-Item -ItemType Directory -Path $downloads -Force | Out-Null
        foreach ($name in @('LinkSafeClientSetup.exe','LinkSafeClientAgent.exe')) {
            $target = Join-Path $downloads $name
            Assert-NoLinks $target
            $temp = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
            Copy-Item -LiteralPath (Join-Path $PayloadRoot ('Downloads\' + $name)) -Destination $temp
            if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp,$target) }
        }
        $helper = Join-Path $toolsDir 'Manage-LinkSafeServer.ps1'
        $ps = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
        New-Item -Path $registryPath -Force | Out-Null
        $values = @{ DisplayName='LinkSafe Server'; DisplayVersion='1.0.21'; Publisher='LinkSafe'; InstallLocation=$installRoot; UninstallString="`"$ps`" -NoProfile -File `"$helper`" -Mode Uninstall" }
        foreach ($key in $values.Keys) { New-ItemProperty -Path $registryPath -Name $key -Value $values[$key] -PropertyType String -Force | Out-Null }
        New-ItemProperty -Path $registryPath -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $registryPath -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
        $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinkSafe Server'
        New-Item -ItemType Directory -Path $menu -Force | Out-Null
        "[InternetShortcut]`r`nURL=http://127.0.0.1:$Port/" | Set-Content -LiteralPath (Join-Path $menu 'LinkSafe Server.url') -Encoding ASCII
        if ($movedPrevious) { Remove-InstallChild $previous }
        Write-Output "Installation complete. Open http://127.0.0.1:$Port/ on this server to create the admin account."
        Write-Output 'Then use Enable LAN access in Setup. No existing config, logs, or backups were removed.'
        Write-Output 'LinkSafeServer is running as an Automatic Windows service. You can close Setup and the browser; backups continue in the background, including after server restart.'
    } elseif ($Mode -eq 'EnableLan') {
        if (-not $ownedInstall) { throw 'Install the server first.' }
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($config.AdminPasswordHash)) { throw 'Create your admin account in the local Web UI before enabling LAN access.' }
        $oldUrl = $config.ServerUrl
        $Port = ([uri]$oldUrl).Port
        if (([uri]$oldUrl).Scheme -ne 'http') { throw 'HTTPS is managed separately; setup will not replace it with HTTP.' }
        $oldRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
        if ($oldRule) {
            if (([uri]$oldUrl).Host -ne '0.0.0.0' -or $oldRule.Enabled -ne 'True') { throw 'A LAN rule already exists but the listener or firewall rule is not enabled. Ask your administrator to review these settings; setup left them unchanged.' }
            Write-Output 'LAN listener and firewall rule already exist. Existing settings were preserved.'
            exit 0
        }
        try {
            Stop-OwnedService
            $config.ServerUrl = "http://0.0.0.0:$Port"
            Save-Json $configPath $config
            Start-Service $serviceName
            Wait-Healthy $Port
            New-NetFirewallRule -Name $ruleName -DisplayName 'LinkSafe Server (trusted LAN)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $exe -Profile Domain,Private -RemoteAddress LocalSubnet | Out-Null
        } catch {
            $failure = $_
            try {
                Stop-OwnedService
                $config.ServerUrl = $oldUrl
                Save-Json $configPath $config
            } catch {
                Write-Warning "Could not restore the previous listener setting: $($_.Exception.Message)"
            } finally {
                # A rollback write failure must not skip the service restart attempt.
                try { Start-Service $serviceName }
                catch { Write-Warning "Could not restart LinkSafeServer: $($_.Exception.Message)" }
            }
            throw $failure
        }
        Write-Output "LAN access enabled on port $Port for the local subnet on Domain/Private networks only."
        Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | ForEach-Object { Write-Output "Server address: http://$($_.IPAddress):$Port/" }
    } elseif ($Mode -eq 'Uninstall') {
        if (-not $ownedInstall) { throw 'This installer does not own the existing installation.' }
        Stop-OwnedService
        if (Get-OwnedService) {
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove the service.' }
        }
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        Remove-InstallChild $serverPath
        if (Test-Path $registryPath) { Remove-Item -Path $registryPath -Recurse }
        $shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LinkSafe Server\LinkSafe Server.url'
        if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
        Write-Output 'Server uninstalled. ALL backups, configuration, client downloads and logs were kept. SetupTools and the ownership marker remain for reinstall.'
    }
} catch {
    Write-Error -Message ("Setup failed at {0}:{1}: {2}" -f $_.InvocationInfo.ScriptName, $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) -ErrorAction Continue
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
