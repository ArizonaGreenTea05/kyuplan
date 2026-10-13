<#
.SYNOPSIS
    Renders the program icon (assets/branding/app.ico) from the tab icon (assets/branding/favicon.svg).

.DESCRIPTION
    The icon of the program file and of the installer is fixed when the program is built, so it is a file, rendered by a
    real browser engine. The generated file is committed, so this only has to run when favicon.svg changes. Needs Microsoft
    Edge (preinstalled on Windows 10/11). The logos need no rendering: both clients draw the SVGs themselves.

    Output (next to the SVG):
      app.ico   application icon: favicon.svg on a tile in the ink colour

    Windows PowerShell 5.1 or PowerShell 7.
#>
[CmdletBinding()]
param(
    [string]$BrandingDir
)

$ErrorActionPreference = 'Stop'
# Resolved here, not as a parameter default: $PSScriptRoot is empty in param() defaults on Windows PowerShell 5.1.
if (-not $BrandingDir) { $BrandingDir = Join-Path $PSScriptRoot '..\assets\branding' }
Add-Type -AssemblyName System.Drawing

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }

$BrandingDir = (Resolve-Path $BrandingDir).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("pp-branding-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function ConvertTo-FileUri([string]$path) { ([Uri]$path).AbsoluteUri }

function Invoke-EdgeScreenshot([string]$html, [string]$png, [int]$width, [int]$height) {
    $page = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($png) + '.html')
    [IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding($false)))
    $profile = Join-Path $work 'edge-profile'
    $edgeArguments = @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', '--no-first-run',
        "`"--user-data-dir=$profile`"", '--default-background-color=00000000',
        "--window-size=$width,$height", "`"--screenshot=$png`"", "`"$(ConvertTo-FileUri $page)`"") -join ' '
    # Start-Process rather than '&': Edge reports on stderr, which Windows PowerShell 5.1 turns into errors.
    Start-Process -FilePath $edge -ArgumentList $edgeArguments -Wait -WindowStyle Hidden
    # Edge returns before the file is flushed on some versions.
    for ($i = 0; $i -lt 50 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $png)) { throw "Edge did not produce $png" }
}

try {
    # App icon: the icon (favicon.svg) in white-outline colours on a rounded square in the ink colour.
    $mono = [IO.File]::ReadAllText((Join-Path $BrandingDir 'favicon.svg')) -replace '<\?xml[^>]*\?>', ''
    $iconHtml = @"
<!doctype html><meta charset="utf-8">
<style>html,body{margin:0;background:transparent;overflow:hidden}
.tile{width:256px;height:256px;border-radius:56px;background:#1B1A1F;display:grid;place-items:center}
.tile svg{width:196px;height:auto;--ink:#F5F0E6 !important;--sun:#E8573F !important}</style>
<div class="tile">$mono</div>
"@
    $tile = Join-Path $work 'icon-256.png'
    Invoke-EdgeScreenshot $iconHtml $tile 256 256

    # Multi-size ICO with PNG-compressed images (supported since Windows Vista).
    $source = [Drawing.Image]::FromFile($tile)
    $images = foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
        $bmp = New-Object Drawing.Bitmap $size, $size
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    }
    $source.Dispose()

    $ico = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ico
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$img.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $img.Bytes.Length
    }
    foreach ($img in $images) { $bw.Write($img.Bytes) }
    $bw.Flush()
    [IO.File]::WriteAllBytes((Join-Path $BrandingDir 'app.ico'), $ico.ToArray())
    "app.ico: {0} sizes, {1:N0} bytes" -f $images.Count, $ico.Length
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
