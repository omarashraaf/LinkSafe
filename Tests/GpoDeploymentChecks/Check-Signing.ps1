$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\Scripts\LinkSafePackageSigning.ps1')
$count = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $script:count++
    Write-Output "PASS: $Name"
}
function Reject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Check $rejected $Name
}
function Certificate {
    [pscustomobject]@{
        HasPrivateKey=$true; NotBefore=(Get-Date).AddDays(-1); NotAfter=(Get-Date).AddDays(10)
        Thumbprint=('A' * 40); EnhancedKeyUsageList=@([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.3'})
    }
}
function Signature {
    [pscustomobject]@{
        Status='Valid'; SignatureType='Authenticode'
        SignerCertificate=[pscustomobject]@{Thumbprint=('A' * 40)}
        TimeStamperCertificate=[pscustomobject]@{Subject='Fixture timestamp'}
    }
}
$cert = Certificate
Assert-LinkSafeSigningCertificate $cert
Check $true 'Currently valid code-signing certificate accepted'
$bad = Certificate; $bad.HasPrivateKey = $false
Reject { Assert-LinkSafeSigningCertificate $bad } 'Private key required'
$bad = Certificate; $bad.NotAfter = (Get-Date).AddSeconds(-1)
Reject { Assert-LinkSafeSigningCertificate $bad } 'Expired certificate rejected'
$bad = Certificate; $bad.NotBefore = (Get-Date).AddDays(1)
Reject { Assert-LinkSafeSigningCertificate $bad } 'Not-yet-valid certificate rejected'
$bad = Certificate; $bad.EnhancedKeyUsageList = @([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.1'})
Reject { Assert-LinkSafeSigningCertificate $bad } 'TLS-only certificate rejected'
$bad = Certificate; $bad.EnhancedKeyUsageList = @()
Reject { Assert-LinkSafeSigningCertificate $bad } 'Missing code-signing purpose rejected'
Assert-LinkSafePackageSignature (Signature) $cert.Thumbprint
Check $true 'Verified embedded signature and timestamp accepted'
foreach ($status in @('NotSigned','HashMismatch','NotTrusted','UnknownError')) {
    $bad = Signature; $bad.Status = $status
    Reject { Assert-LinkSafePackageSignature $bad $cert.Thumbprint } "Signature $status rejected"
}
$bad = Signature; $bad.SignatureType = 'Catalog'
Reject { Assert-LinkSafePackageSignature $bad $cert.Thumbprint } 'Catalog signature cannot substitute for an embedded signature'
$bad = Signature; $bad.SignerCertificate.Thumbprint = 'B' * 40
Reject { Assert-LinkSafePackageSignature $bad $cert.Thumbprint } 'Wrong release signer rejected'
$bad = Signature; $bad.TimeStamperCertificate = $null
Reject { Assert-LinkSafePackageSignature $bad $cert.Thumbprint } 'Missing timestamp rejected'

# Mock only the signing boundary. No private keys, certificate stores or network are used.
$script:signCalls = 0; $script:readCalls = 0; $script:badRead = $false; $script:badSign = $false
function Set-AuthenticodeSignature {
    param($LiteralPath, $Certificate, $HashAlgorithm, $TimestampServer, $ErrorAction)
    if ($LiteralPath -ne 'fixture.exe' -or $HashAlgorithm -ne 'SHA256' -or $TimestampServer -ne 'https://timestamp.example.invalid') {
        throw 'Unexpected signing arguments'
    }
    $script:signCalls++
    $result = Signature
    if ($script:badSign) { $result.Status = 'NotTrusted' }
    return $result
}
function Get-AuthenticodeSignature {
    param($LiteralPath, $ErrorAction)
    $script:readCalls++
    $result = Signature
    if ($script:badRead) { $result.Status = 'HashMismatch' }
    return $result
}
Set-LinkSafePackageSignature 'fixture.exe' $cert 'https://timestamp.example.invalid'
Check ($signCalls -eq 1 -and $readCalls -eq 1) 'Signature re-read and verified after signing'
$script:badRead = $true
Reject { Set-LinkSafePackageSignature 'fixture.exe' $cert 'https://timestamp.example.invalid' } 'Post-signing mismatch fails closed'
$script:badRead = $false; $script:badSign = $true
$before = $readCalls
Reject { Set-LinkSafePackageSignature 'fixture.exe' $cert 'https://timestamp.example.invalid' } 'Signing failure fails closed'
Check ($readCalls -eq $before) 'No fallback after failed signing'
$script:badSign = $false
foreach ($url in @('file:///C:/timestamp','not-a-url','https://user:password@example.invalid','https://example.invalid/#fragment')) {
    $before = $signCalls
    Reject { Set-LinkSafePackageSignature 'fixture.exe' $cert $url } "Invalid timestamp URL rejected"
    Check ($signCalls -eq $before) 'Invalid timestamp URL never reaches the signer'
}
Write-Output "All $count signing checks passed. No real files were signed or submitted, and no certificates were created or trusted."
