# Generates Assets\DevShell.ico (multi-size) from Assets\DevShell.png.
#
# Assets\DevShell.png IS the product icon: it is the file the executable, the shell and the
# setup all wear, and it is committed alongside this script. Nothing reads an icon off the
# user's desktop at run time any more, so the icon a build ships with is the icon in the repo.
#
# Frames are classic DIB payloads so every consumer (GDI+, WPF, the shell, rc.exe) can read
# them; the 256px frame is PNG-compressed, which is the normal convention.
# Fails loudly: a silent error here once produced a blank icon.
param(
    [string]$Source = '',
    [string]$Preview = (Join-Path ([System.IO.Path]::GetTempPath()) 'devshell-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $root 'Assets\DevShell.png' }
$outDir = Join-Path $root 'Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir 'DevShell.ico'

if (-not (Test-Path -LiteralPath $Source)) {
    throw "icon source not found: $Source"
}

$src = [System.Drawing.Image]::FromFile($Source)
try {
    if ($src.Width -ne $src.Height) {
        throw "icon source must be square, got $($src.Width)x$($src.Height)"
    }
    if ($src.Width -lt 256) {
        throw "icon source should be at least 256x256, got $($src.Width)x$($src.Height)"
    }

    # Draw the source down to one size. HighQualityBicubic plus a transparent clear keeps the
    # artwork's own alpha; the source is never stretched up, so nothing is invented.
    function New-Scaled([System.Drawing.Image]$image, [int]$size) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        # the destination rect is inset by half a pixel so the resampler does not smear the border
        $rect = New-Object System.Drawing.RectangleF(0, 0, [single]$size, [single]$size)
        $g.DrawImage($image, $rect)
        $g.Dispose()
        return $bmp
    }

    # Pull the raw BGRA payload out of a 32bpp BMP that GDI+ wrote for us.
    function Get-BgraPayload([System.Drawing.Bitmap]$bmp) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Bmp)
        $bytes = $ms.ToArray()
        $ms.Dispose()
        $offBits = [BitConverter]::ToUInt32($bytes, 10)   # bfOffBits
        $len = $bytes.Length - $offBits
        $payload = New-Object byte[] $len
        [Array]::Copy($bytes, $offBits, $payload, 0, $len)
        # -NoEnumerate: without it PowerShell unrolls the byte[] into object[]
        Write-Output -NoEnumerate $payload
    }

    function New-DibFrame([System.Drawing.Bitmap]$bmp, [int]$size) {
        [byte[]]$pixels = Get-BgraPayload $bmp
        $maskStride = [Math]::Floor(($size + 31) / 32) * 4
        $maskSize = $maskStride * $size
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([UInt32]40)                              # biSize
        $bw.Write([Int32]$size)                            # biWidth
        $bw.Write([Int32]($size * 2))                      # biHeight (XOR + AND)
        $bw.Write([UInt16]1)                               # biPlanes
        $bw.Write([UInt16]32)                              # biBitCount
        $bw.Write([UInt32]0)                               # biCompression = BI_RGB
        $bw.Write([UInt32]($size * $size * 4 + $maskSize)) # biSizeImage
        $bw.Write([Int32]0); $bw.Write([Int32]0)           # pels per meter
        $bw.Write([Int32]0); $bw.Write([Int32]0)           # clrUsed / clrImportant
        $bw.Write($pixels)
        $bw.Write([byte[]]::new($maskSize))                # AND mask: all zero (use alpha)
        $bw.Flush()
        $data = $ms.ToArray()
        $bw.Dispose(); $ms.Dispose()
        Write-Output -NoEnumerate $data
    }

    function New-PngFrame([System.Drawing.Bitmap]$bmp) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
        $ms.Dispose()
        Write-Output -NoEnumerate $data
    }

    $frames = @()
    foreach ($sz in @(16, 20, 24, 32, 40, 48, 64, 128)) {
        $bmp = New-Scaled $src $sz
        [byte[]]$data = New-DibFrame $bmp $sz
        $frames += , @{ Size = $sz; Data = $data }
        $bmp.Dispose()
    }
    $big = New-Scaled $src 256
    [byte[]]$pngData = New-PngFrame $big
    $frames += , @{ Size = 256; Data = $pngData }
    # preview is rendered from the same pixels, so what I look at is what ships
    $big.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    $big.Dispose()

    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$f.Data.Length); $bw.Write([UInt32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Data) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()

    # read it back the way Windows will, to prove the container is well formed
    $in = [System.IO.File]::ReadAllBytes($icoPath)
    $count = [BitConverter]::ToUInt16($in, 4)
    Write-Output "source: $Source"
    Write-Output "ico: $icoPath  ($($in.Length) bytes, $count frames)"
    for ($i = 0; $i -lt $count; $i++) {
        $b = 6 + 16 * $i
        $w = $in[$b]; if ($w -eq 0) { $w = 256 }
        $len = [BitConverter]::ToUInt32($in, $b + 8)
        $off = [BitConverter]::ToUInt32($in, $b + 12)
        $ok = ($off + $len) -le $in.Length
        $kind = if ($in[$off] -eq 0x89 -and $in[$off + 1] -eq 0x50) { 'png' } else { 'dib' }
        Write-Output ("  frame {0}: {1}x{1} {2} len={3} off={4} inBounds={5}" -f $i, $w, $kind, $len, $off, $ok)
    }
    Write-Output "preview: $Preview"
}
finally {
    $src.Dispose()
}
