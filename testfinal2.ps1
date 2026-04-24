Add-Type -AssemblyName System.Drawing

$exePath = "C:\ProgramData\LinkSafeServer\Downloads\LinkSafeClientSetup.exe"
Write-Host "Testing: $exePath"
Write-Host "Exists: $([System.IO.File]::Exists($exePath))"
Write-Host "Size: $((Get-Item $exePath).Length)"

$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)
if ($icon) {
    Write-Host "SUCCESS: Icon extracted from exe"
    Write-Host "Size: $($icon.Size)"
} else {
    Write-Host "FAILED"
}