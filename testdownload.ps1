Add-Type -AssemblyName System.Drawing

$exePath = "C:\ProgramData\LinkSafeServer\Downloads\LinkSafeClientSetup.exe"
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)

if ($icon) {
    Write-Host "SUCCESS: Icon extracted from downloaded exe"
    Write-Host "Size: $($icon.Size)"
    Write-Host "Handle: $($icon.Handle)"
} else {
    Write-Host "FAILED: Icon not found"
}