$srcPng = 'E:\LinkSafe\icon.png'
$dstIco = 'E:\LinkSafe\LinkSafe\LinkSafeClient\LinkSafeClientAgent\app.ico'

Add-Type -AssemblyName System.Drawing

$bmp = [System.Drawing.Bitmap]::new($srcPng)
$ico = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$ms = [System.IO.MemoryStream]::new()
$ico.Save($ms)
[System.IO.File]::WriteAllBytes($dstIco, $ms.ToArray())
$ms.Close()
$bmp.Dispose()

Write-Host "Created at $dstIco"