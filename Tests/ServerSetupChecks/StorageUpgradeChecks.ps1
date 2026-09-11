$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../Scripts/ServerSetup/Manage-LinkSafeServer.ps1')
$fixture = Join-Path $PSScriptRoot ('../../artifacts/server-setup-tests/' + [guid]::NewGuid().ToString('N'))
$fixture = [IO.Path]::GetFullPath($fixture)
$root = Join-Path $fixture 'UsersBackup'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected 'Unsafe or unverified storage was accepted.'
}
$shares = @([pscustomobject]@{ Name='UsersBackup$'; Path=$root; ScopeName='*'; Scoped=$false })
$names = @('FIXTURE-SERVER','192.0.2.10','localhost')
$unc = '\\192.0.2.10\UsersBackup$'
Check ((Resolve-LocalSharePath $unc $names $shares) -eq $root) 'Local IP share was not resolved.'
Check ((Resolve-LocalSharePath '\\fixture-server\UsersBackup$\team' $names $shares) -eq (Join-Path $root 'team')) 'Hostname or suffix mapping failed.'
Check ((Assert-BackupPath $root) -eq $root) 'Valid local backup path was rejected.'
Reject { Resolve-LocalSharePath '\\other-server\UsersBackup$' $names $shares }
Reject { Resolve-LocalSharePath '\\192.0.2.10\Missing$' $names $shares }
Reject { Resolve-LocalSharePath $unc $names @($shares[0], $shares[0]) }
Reject { Resolve-LocalSharePath $unc $names @([pscustomobject]@{ Name='UsersBackup$'; Path=$root; ScopeName='*'; Scoped=$true }) }
Reject { Resolve-LocalSharePath $unc $names @([pscustomobject]@{ Name='UsersBackup$'; Path='\\other\data'; ScopeName='*' }) }
foreach ($suffix in @('\..\escape','\file:ads','\folder.\file','\CON')) {
    Reject { Assert-BackupPath (Resolve-LocalSharePath ($unc + $suffix) $names $shares) }
}
Reject { Assert-BackupPath $unc }
Reject { Assert-BackupPath 'relative\backups' }
Reject { Assert-BackupPath ([IO.Path]::GetPathRoot($root)) }
# Inject local inventory only; no real share is created or queried by these tests.
function Get-NetIPAddress { [pscustomobject]@{ IPAddress='192.0.2.10' } }
function Get-SmbShare { $shares }
Check ((Get-UpgradeBackupPath $unc) -eq $root) 'Upgrade did not validate the local share folder.'
Check ((Get-UpgradeBackupPath $root) -eq $root) 'Local upgrades should keep the same folder.'
Reject { Get-UpgradeBackupPath ($unc + '\missing-folder') }
$config = [pscustomobject]@{ BackupRoot=$unc; AdminUsername='fixture-admin'; AdminPasswordHash='fixture-hash'; ServerUrl='http://0.0.0.0:5055'; CustomFolders=@('D:\Projects') }
$converted = Convert-StorageConfig $config $root
Check ($config.BackupRoot -eq $unc -and $converted.BackupRoot -eq $root) 'Conversion mutated the original config.'
Check ($converted.IncomingRoot -eq (Join-Path $root '_Incoming') -and $converted.QuarantineRoot -eq (Join-Path $root '_Quarantine')) 'System directories were not rebased.'
Check ($converted.AdminPasswordHash -eq $config.AdminPasswordHash -and $converted.ServerUrl -eq $config.ServerUrl -and $converted.CustomFolders[0] -eq 'D:\Projects') 'Other configuration changed.'
$configFile = Join-Path $fixture 'config.json'
Save-Json $configFile $converted
Save-Json $configFile $config
Check ((Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json).BackupRoot -eq $unc) 'Config rollback snapshot could not be saved.'
$clients = @([pscustomobject]@{ BackupDestinationPath=($unc + '\alice\PC01'); Username='alice'; IsDisabled=$true })
$mapped = @(Convert-ClientDestinations $clients $unc $root)
Check ($mapped.Count -eq 1 -and $mapped[0].BackupDestinationPath -eq (Join-Path $root 'alice\PC01') -and $mapped[0].IsDisabled) 'Client restore destination mapping failed.'
Check ($clients[0].BackupDestinationPath.StartsWith($unc)) 'Original registry was mutated.'
Reject { Convert-ClientDestinations @([pscustomobject]@{ BackupDestinationPath=($unc + '\..\outside') }) $unc $root }
$file = Join-Path $fixture 'clients.json'
Save-Json $file $mapped
Check ((Get-Content -LiteralPath $file -Raw).TrimStart().StartsWith('[')) 'One client must remain a JSON array.'
Save-Json $file @()
Check ((Get-Content -LiteralPath $file -Raw).Trim() -eq '[]') 'Empty registry must remain a JSON array.'
Save-Json $file $clients
Check (((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)[0]).BackupDestinationPath -eq $clients[0].BackupDestinationPath) 'Rollback snapshot could not be saved.'
Check (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) 'Migration unexpectedly changed backup files.'
$helper = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Scripts/ServerSetup/Manage-LinkSafeServer.ps1') -Raw
Check ($helper.Contains('Set-Service -Name $serviceName -StartupType Automatic')) 'Upgrades must enforce automatic startup.'
foreach ($count in @(0,1,2,10)) {
    $records = @(for ($i=0; $i -lt $count; $i++) {
        [pscustomobject]@{ BackupDestinationPath=($unc + '\user' + $i + '\PC' + $i); Username=('user' + $i); IsDisabled=($i -eq 1) }
    })
    Save-Json $file $records
    $loaded = @(Read-ClientRegistry $file)
    Check ($loaded.Count -eq $count) "Wrong loaded client count: $count"
    $migrated = @(Convert-ClientDestinations $loaded $unc $root)
    Save-Json $file $migrated
    $roundtrip = @(Read-ClientRegistry $file)
    Check ($roundtrip.Count -eq $count) "Wrong saved client count: $count"
    for ($i=0; $i -lt $count; $i++) {
        Check ($roundtrip[$i].BackupDestinationPath -eq (Join-Path $root ('user' + $i + '\PC' + $i))) 'Client paths were combined or reordered.'
        Check ($roundtrip[$i].IsDisabled -eq ($i -eq 1)) 'Client status changed.'
    }
    Save-Json $file $loaded
    Check ((Get-Content -LiteralPath $file -Raw).Contains($root.Replace('\','\\')) -eq $false) 'Rollback retained a migrated path.'
}
Save-Json $file ([pscustomobject]@{ BackupDestinationPath=$root })
Reject { Read-ClientRegistry $file }
Save-Json $file @($null)
Reject { Read-ClientRegistry $file }
Save-Json $file @(@([pscustomobject]@{ BackupDestinationPath=$root }), @([pscustomobject]@{ BackupDestinationPath=$root }))
Reject { Read-ClientRegistry $file }
Check ($helper.IndexOf('$previewClients = @(Read-ClientRegistry $clientsPath)') -lt $helper.IndexOf("if (`$Mode -eq 'Validate') { exit 0 }")) 'Migration preflight must run before extraction or service changes.'
Write-Output "PASS: $script:checks storage upgrade checks. No live configuration, services, shares or backups changed."
