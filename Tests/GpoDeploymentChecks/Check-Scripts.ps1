$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$count = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $script:count++
    Write-Output "PASS: $Name"
}
foreach ($name in @('Deploy-LinkSafeClientGpo.ps1','Build-LinkSafeGpoPackage.ps1','LinkSafePackageSigning.ps1')) {
    $tokens = $null; $errors = $null
    $path = Join-Path $root ('Scripts\' + $name)
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) "PowerShell parser accepts $name"
    $text = [IO.File]::ReadAllText($path)
    Check ($text -notmatch '(?i)-ExecutionPolicy\s+(Bypass|Unrestricted)|Set-ExecutionPolicy') 'No execution-policy bypass'
    Check ($text -notmatch '(?i)Remove-Item[^\r\n]*-Recurse') 'No recursive removal'
    if ($name -eq 'Deploy-LinkSafeClientGpo.ps1') {
        Check ($text.Contains('WindowStyle Hidden')) 'Setup launched hidden'
        Check ($text.Contains('IsSystem') -and $text.Contains('SessionId -eq 0')) 'SYSTEM/session-zero guard present'
        Check ($text.Contains('NullString') -and $text.Contains('File]::Replace')) 'Windows PowerShell null-string replacement retained'
        Check ($text.Contains('Test-Hash $temporary $hash')) 'Copied payload verified before execution'
        Check ($text.Contains('WaitForExit(180000)') -and $text.Contains('$process.ExitCode')) 'Bounded wait and checked installer exit'
        Check ($text.Contains('default-folders-fixed-drives-v1')) 'Stable one-time baseline ID default'
    }
    if ($name -eq 'Build-LinkSafeGpoPackage.ps1') {
        Check ($text.Contains("DefaultParameterSetName='Signed'")) 'Distribution requires signing by default'
        Check ($text.Contains('AllowUnsignedDevelopmentPackage')) 'Unsigned output is an explicit development choice'
        Check ($text.IndexOf('Set-LinkSafePackageSignature -Path') -lt $text.IndexOf('$hashes[$name] =')) 'Hash computation follows signing'
        Check ($text.Contains("'Deploy-LinkSafeClientGpo.ps1'))")) 'Deployment script is included in the signing loop'
        Check ($text.Contains("'artifacts\GpoDeployment\' + " + '$buildId')) 'Each package has its own release directory'
    }
}
Write-Output "All $count script checks passed on PowerShell $($PSVersionTable.PSVersion). No deployment executed."
