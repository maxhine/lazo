param([Parameter(Mandatory=$true)][int]$ProcessId,
      [Parameter(Mandatory=$true)][string]$OutputPath,
      [string]$WindowTitle = 'Lazo',
      [switch]$CheckPlacement)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LazoCapture {
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$script:window = [IntPtr]::Zero
$callback = [LazoCapture+EnumWindowsProc] {
    param($handle, $ignored)
    $id = [uint32]0
    [LazoCapture]::GetWindowThreadProcessId($handle, [ref]$id) | Out-Null
    if ($id -eq [uint32]$ProcessId) {
        $title = [System.Text.StringBuilder]::new(256)
        [LazoCapture]::GetWindowText($handle, $title, 256) | Out-Null
        if ($title.ToString() -eq $WindowTitle) { $script:window = $handle }
    }
    return $true
}
[LazoCapture]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
if ($script:window -eq [IntPtr]::Zero) { throw 'No se encontró la ventana de Lazo.' }
$rectangle = [LazoCapture+Rect]::new()
[LazoCapture]::GetWindowRect($script:window, [ref]$rectangle) | Out-Null
if ($CheckPlacement) {
    Add-Type -AssemblyName System.Windows.Forms
    $area = [System.Windows.Forms.Screen]::FromPoint([System.Drawing.Point]::new([int](($rectangle.Left + $rectangle.Right) / 2), [int](($rectangle.Top + $rectangle.Bottom) / 2))).WorkingArea
    $windowCenterX = ($rectangle.Left + $rectangle.Right) / 2
    $areaCenterX = ($area.Left + $area.Right) / 2
    if ([math]::Abs($windowCenterX - $areaCenterX) -gt 8 -or
        [math]::Abs($rectangle.Bottom - ($area.Bottom - 4)) -gt 8) {
        throw "La ventana no está en la base central de la pantalla del launcher."
    }
    Write-Host "Posición: base central de la pantalla del launcher ($($area.Width)x$($area.Height))."
}
$bitmap = [System.Drawing.Bitmap]::new($rectangle.Right-$rectangle.Left, $rectangle.Bottom-$rectangle.Top)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    if (![LazoCapture]::PrintWindow($script:window, $hdc, 2)) { throw 'PrintWindow falló.' }
} finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}
$bitmap.Save($OutputPath)
$bitmap.Dispose()
Write-Host "Captura: $OutputPath"
