# Builds the island's installer: release\Lazo-Windows-<version>-setup.exe, with the look of the classic Lazo's installer
# (installer\Setup.cs is derived from lazo\installer\Setup.cs). The island, the classic Lazo it carries, the tools program and
# Topcon's tps2rin.exe travel inside it as a zip. It installs to Program Files\Lazo Isla, next to (never over) the classic Lazo.
param([switch]$SkipBuild, [switch]$Preview, [string]$Name = '')

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $windows
$version = (Get-Content (Join-Path $windows 'package.json') -Raw | ConvertFrom-Json).version
if (!$SkipBuild) {
    Push-Location $windows
    try {
        npx tauri build --no-bundle
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la isla.' }
    } finally { Pop-Location }
}

$release = Join-Path $windows 'target\release'
$files = [ordered]@{
    'lazo.exe'                  = Join-Path $release 'lazo.exe'
    'lazo-hook.exe'             = Join-Path $release 'lazo-hook.exe'
    'classic\Lazo.exe'          = Join-Path $root 'lazo\bin\Lazo.exe'
    'engine\Lazo.Engine.exe'    = Join-Path $root 'engine\bin\Lazo.Engine.exe'
    'engine\tps2rin.exe'        = Join-Path $root 'engine\tps2rin\tps2rin.exe'
}
foreach ($entry in $files.GetEnumerator()) { if (!(Test-Path -LiteralPath $entry.Value)) { throw "Falta $($entry.Value)" } }

$work = Join-Path $windows 'target\installer'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
$payload = Join-Path $work 'payload'
foreach ($entry in $files.GetEnumerator()) {
    $destination = Join-Path $payload $entry.Key
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $entry.Value -Destination $destination
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') { 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' } else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$dist = Join-Path $windows 'release'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$target = Join-Path $dist $(if ($Name) { $Name } elseif ($Preview) { 'Lazo-Isla-Setup-Preview.exe' } else { "Lazo-Windows-$version-setup.exe" })
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/utf8output', '/optimize+', ('/out:' + $target), ('/resource:' + $zip + ',Lazo.Payload'),
               '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll')
$gac = 'C:\Windows\Microsoft.NET\assembly'
foreach ($assembly in @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml')) {
    $match = Get-ChildItem -LiteralPath $gac -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $assembly -ErrorAction SilentlyContinue
    } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "$assembly.dll" -File -Recurse -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (!$match) { throw "No se encontró $assembly.dll" }
    $arguments += '/reference:' + $match.FullName
}
$icon = Join-Path $windows 'src-tauri\icons\icon.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon }
if (!$Preview) { $arguments += '/win32manifest:' + (Join-Path $windows 'installer\app.manifest') }
$arguments += (Join-Path $windows 'installer\Setup.cs')
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación del instalador (código $LASTEXITCODE)." }
Write-Host "Instalador: $target ($([math]::Round((Get-Item $target).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $((Get-FileHash $target).Hash.ToLower())"
