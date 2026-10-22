# Compila Lazo.Engine.exe: el motor de Lazo sin interfaz. Reutiliza tal cual los archivos de ../lazo/src
# (red, cifrado, chat, nubes de puntos, Xpace); no toca el Lazo clásico. Requiere .NET Framework 4.8 (csc, C# 5).
param([switch]$Release, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$shared = Join-Path (Split-Path -Parent $root) 'lazo\src'
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'bin' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.8 es necesario para compilar el motor.' }

# Archivos del Lazo clásico que no dependen de WPF.
$core = 'AppInfo','ChatEvents','ChatStore','CloudExport','CloudReaders','CloudReprojector','CoordinateSystems','DiagLog','Geodesy',
        'Identity','NetworkEngine','PointCloudConverter','ProfilePhoto','RemoteAccess','SecureChannel','Startup','TransferLog',
        'Updater','ViewerPrefs','XpaceActions','XpaceScanner','CloudPreview','EyeCare','Theme','Icons','WindowPlacement','ImageTheme','HeroBackground'
$sources = @($core | ForEach-Object { Join-Path $shared "$_.cs" }) + (Join-Path $root 'EngineHost.cs') + (Join-Path $root 'EngineProgram.cs') + (Join-Path $root 'XpaceTool.cs') + (Join-Path $root 'CloudTool.cs') + (Join-Path $root 'EyeCareTool.cs')
$arguments = @('/nologo', '/target:exe', '/platform:anycpu', '/utf8output', '/warn:4', ('/out:' + (Join-Path $output 'Lazo.Engine.exe')))
$arguments += if ($Release) { '/optimize+' } else { '/debug+' }
$arguments += '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Drawing.dll',
              '/reference:System.Windows.Forms.dll', '/reference:Microsoft.VisualBasic.dll', '/reference:System.Web.Extensions.dll'
# CloudPreview dibuja con WPF (solo imágenes, sin ventanas).
$gac = 'C:\Windows\Microsoft.NET\assembly'
foreach ($name in @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml')) {
    $match = Get-ChildItem -LiteralPath $gac -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $name -ErrorAction SilentlyContinue
    } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "$name.dll" -File -Recurse -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (!$match) { throw "No se encontró $name.dll" }
    $arguments += '/reference:' + $match.FullName
}
$arguments += $sources
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación (código $LASTEXITCODE)." }
Write-Host "Compilado: $(Join-Path $output 'Lazo.Engine.exe')"
