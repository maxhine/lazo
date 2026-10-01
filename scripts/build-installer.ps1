param([switch]$Preview)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Release
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar Lazo.' }
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$dist = Join-Path $project 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$name = if ($Preview) { 'Lazo-Setup-Preview.exe' } else { 'Lazo-Setup-0.4.14.exe' }
$target = Join-Path $dist $name
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/utf8output','/optimize+',('/out:' + $target),
    ('/resource:' + (Join-Path $project 'bin\Lazo.exe') + ',Lazo.Payload'))
$icon = Join-Path $project 'assets\lazo.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon }
if (!$Preview) { $arguments += '/win32manifest:' + (Join-Path $project 'installer\app.manifest') }
$arguments += (Join-Path $project 'installer\Setup.cs')
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación del instalador (código $LASTEXITCODE)." }
Write-Host "Instalador: $target"
