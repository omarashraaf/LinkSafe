[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ('artifacts\server-installer\' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'Payload'
$output = Join-Path $root 'artifacts\ServerSetup'
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null

function Publish-Project([string]$Project, [string]$Destination, [bool]$SingleFile) {
    & dotnet publish (Join-Path $root $Project) -c $Configuration -r win-x64 --self-contained true -o $Destination "-p:PublishSingleFile=$SingleFile" '-p:IncludeNativeLibrariesForSelfExtract=true'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}
Publish-Project 'LinkSafe\LinkSafeServer\LinkSafeServer.csproj' (Join-Path $payload 'Server') $false
Publish-Project 'LinkSafe\LinkSafeClient\LinkSafeClientAgent\LinkSafeClientAgent.csproj' (Join-Path $stage 'Agent') $true
Publish-Project 'LinkSafe\LinkSafeClient\LinkSafeClientSetup\LinkSafeClientSetup.csproj' (Join-Path $stage 'ClientSetup') $true
$downloads = New-Item -ItemType Directory -Path (Join-Path $payload 'Downloads')
Copy-Item -LiteralPath (Join-Path $stage 'Agent\LinkSafeClientAgent.exe') -Destination $downloads.FullName
Copy-Item -LiteralPath (Join-Path $stage 'ClientSetup\LinkSafeClientSetup.exe') -Destination $downloads.FullName
# Only freshly published binaries are packaged. No live config, identity, or backup data.
$manifest = [ordered]@{}
Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
    $manifest[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding UTF8
$archive = Join-Path $stage 'payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
& dotnet publish (Join-Path $root 'Installer\LinkSafeServerSetup\LinkSafeServerSetup.csproj') -c $Configuration -o $output "-p:PayloadPath=$archive"
if ($LASTEXITCODE -ne 0) { throw 'Server setup publish failed.' }
$setup = Join-Path $output 'LinkSafeServerSetup.exe'
Get-Item -LiteralPath $setup | Select-Object FullName,Length,LastWriteTime
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
"$hash  LinkSafeServerSetup.exe" | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ASCII
Write-Output "SHA-256: $hash"
Write-Host 'Setup is unsigned. Test installation on a clean Windows x64 VM before deploying.'
