#Requires -Version 7.3
[CmdletBinding(DefaultParameterSetName='Signed')]
param(
    [string]$Configuration = 'Release',
    [Parameter(Mandatory, ParameterSetName='Signed')]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName='Signed')][string]$TimestampServer,
    [Parameter(Mandatory, ParameterSetName='Development')][switch]$AllowUnsignedDevelopmentPackage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LinkSafePackageSigning.ps1')
$certificate = $null
if ($PSCmdlet.ParameterSetName -eq 'Signed') {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningCertificateThumbprint" -ErrorAction Stop
    Assert-LinkSafeSigningCertificate $certificate
} elseif (-not $AllowUnsignedDevelopmentPackage) {
    throw 'Unsigned output requires explicit development-only consent.'
} else {
    Write-Warning 'UNSIGNED DEVELOPMENT PACKAGE: do not deploy to domain users. No antivirus approval is implied.'
}
$root = Split-Path -Parent $PSScriptRoot
$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$stage = Join-Path $root ('artifacts\gpo-build\' + $buildId)
$output = Join-Path $root ('artifacts\GpoDeployment\' + $buildId)
$package = Join-Path $stage 'package'
New-Item -ItemType Directory -Path $stage,$package -Force | Out-Null
$hashes = [ordered]@{}
foreach ($app in @('LinkSafeClientSetup','LinkSafeClientAgent')) {
    $project = Join-Path $root "LinkSafe\LinkSafeClient\$app\$app.csproj"
    $destination = Join-Path $stage $app
    & dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $destination '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:UseSharedCompilation=false'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $app" }
    $file = Join-Path $destination "$app.exe"
    Copy-Item -LiteralPath $file -Destination $package
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Deploy-LinkSafeClientGpo.ps1') -Destination $package
foreach ($name in @('LinkSafeClientSetup.exe','LinkSafeClientAgent.exe','Deploy-LinkSafeClientGpo.ps1')) {
    $file = Join-Path $package $name
    if ($certificate) { Set-LinkSafePackageSignature -Path $file -Certificate $certificate -TimestampServer $TimestampServer }
    # Signing changes bytes: the manifest must describe the final signed artifact.
    $hashes[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
$signing = if ($certificate) {
    [ordered]@{ Mode='Authenticode'; Publisher=$certificate.Subject; Thumbprint=$certificate.Thumbprint; CertificateExpires=$certificate.NotAfter.ToUniversalTime().ToString('o'); TimestampServer=$TimestampServer }
} else { [ordered]@{ Mode='UnsignedDevelopmentOnly' } }
[ordered]@{ SchemaVersion=1; CreatedAt=[DateTimeOffset]::UtcNow.ToString('o'); Hashes=$hashes; Signing=$signing } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'package.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $root 'docs\GPO-Deployment.md') -Destination (Join-Path $package 'README.md')
# Never replace a previous release or a live share. A manifest appears only after all copies verify.
New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
foreach ($name in @('LinkSafeClientSetup.exe','LinkSafeClientAgent.exe','Deploy-LinkSafeClientGpo.ps1','README.md')) {
    $target = Join-Path $output $name
    Copy-Item -LiteralPath (Join-Path $package $name) -Destination $target
    if ($name -ne 'README.md' -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$name]) {
        throw "Output verification failed: $name. No release manifest was published."
    }
}
Copy-Item -LiteralPath (Join-Path $package 'package.json') -Destination $output
Get-ChildItem -LiteralPath $output -File | Select-Object Name,Length
Write-Host "GPO kit prepared: $output. Signing mode: $($signing.Mode). Endpoint security review and pilot approval are still required. Nothing was installed, shared or linked to a domain."
