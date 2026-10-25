param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'NotificationAssets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($asset in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
    $size = [int]$asset[1]
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 24, 32))
    $island = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(92, 215, 192))
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(248, 250, 252))
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.FillEllipse($background, 0, 0, $size - 1, $size - 1)
        $width = [single]($size * 0.24)
        $left = [single]($size * 0.30)
        $top = [single]($size * 0.25)
        $height = [single]($size * 0.50)
        $graphics.FillRectangle($island, $left, $top + $width / 2, $width, $height - $width)
        $graphics.FillEllipse($island, $left, $top, $width, $width)
        $graphics.FillEllipse($island, $left, $top + $height - $width, $width, $width)
        $graphics.FillEllipse($dot, [single]($size * 0.62), [single]($size * 0.32), [single]($size * 0.12), [single]($size * 0.12))
        $bitmap.Save((Join-Path $OutputDirectory $asset[0]), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $dot.Dispose(); $island.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
Write-Output ('Notification identity assets: ' + [IO.Path]::GetFullPath($OutputDirectory))
