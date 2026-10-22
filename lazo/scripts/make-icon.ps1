$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class IconHandle { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle); }
'@
$project = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $project 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$bitmap = [System.Drawing.Bitmap]::new(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(35,35,35))
$white = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 4)
$white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.FillEllipse($dark, 3, 3, 58, 58)
$graphics.DrawLine($white, 16, 23, 46, 23)
$graphics.DrawLine($white, 39, 16, 46, 23)
$graphics.DrawLine($white, 46, 23, 39, 30)
$graphics.DrawLine($white, 48, 41, 18, 41)
$graphics.DrawLine($white, 25, 34, 18, 41)
$graphics.DrawLine($white, 18, 41, 25, 48)
$graphics.Dispose()
$dark.Dispose()
$white.Dispose()
$handle = $bitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Create((Join-Path $assets 'lazo.ico'))
    try { $icon.Save($stream) } finally { $stream.Dispose() }
} finally {
    [IconHandle]::DestroyIcon($handle) | Out-Null
    $bitmap.Dispose()
}
