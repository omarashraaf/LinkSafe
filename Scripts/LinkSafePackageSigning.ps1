# Build-time helpers only. These functions never install certificates or change endpoint policy.
function Assert-LinkSafeSigningCertificate {
    param([Parameter(Mandatory)]$Certificate)
    if (-not $Certificate.HasPrivateKey) { throw 'The selected signing certificate has no accessible private key.' }
    $now = Get-Date
    if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) {
        throw 'The selected signing certificate is not currently valid.'
    }
    if ('1.3.6.1.5.5.7.3.3' -notin @($Certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })) {
        throw 'The selected certificate must explicitly allow Code Signing.'
    }
}

function Assert-LinkSafePackageSignature {
    param([Parameter(Mandatory)]$Signature, [Parameter(Mandatory)][string]$ExpectedThumbprint)
    if ([string]$Signature.Status -ne 'Valid' -or [string]$Signature.SignatureType -ne 'Authenticode') {
        throw "An embedded Authenticode signature could not be verified: $($Signature.Status)."
    }
    if (-not $Signature.SignerCertificate -or $Signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint) {
        throw 'The artifact signer does not match the selected release certificate.'
    }
    if (-not $Signature.TimeStamperCertificate) { throw 'The artifact has no verified timestamp. Release was stopped.' }
}

function Set-LinkSafePackageSignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][string]$TimestampServer
    )
    Assert-LinkSafeSigningCertificate $Certificate
    $uri = $null
    if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo -or $uri.Fragment) {
        throw 'Specify the approved Authenticode timestamp service URL without credentials or a fragment.'
    }
    $signed = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $Certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer -ErrorAction Stop
    Assert-LinkSafePackageSignature $signed $Certificate.Thumbprint
    # Re-read the file rather than relying only on the signing operation's return value.
    Assert-LinkSafePackageSignature (Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop) $Certificate.Thumbprint
}
