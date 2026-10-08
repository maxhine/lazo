param([switch]$Preview,[switch]$SkipBuild,[string]$BinDirectory = '',[string]$Name = '')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$bin = if ([string]::IsNullOrWhiteSpace($BinDirectory)) { Join-Path $project 'bin' } elseif ([IO.Path]::IsPathRooted($BinDirectory)) { $BinDirectory } else { Join-Path $project $BinDirectory }
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Release -OutputDirectory $bin
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar Lazo.' }
}
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$dist = Join-Path $project 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$name = if ($Name) { $Name } elseif ($Preview) { 'Lazo-Setup-Preview.exe' } else { 'Lazo-Setup-0.4.16.exe' }
$target = Join-Path $dist $name
$application = Join-Path $bin 'Lazo.exe'
if (!(Test-Path -LiteralPath $application)) { throw 'No existe el binario de Lazo; compila antes de crear el instalador.' }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/utf8output','/optimize+',('/out:' + $target),
    ('/resource:' + $application + ',Lazo.Payload'))
$icon = Join-Path $project 'assets\lazo.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon }
if (!$Preview) { $arguments += '/win32manifest:' + (Join-Path $project 'installer\app.manifest') }
$arguments += (Join-Path $project 'installer\Setup.cs')
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación del instalador (código $LASTEXITCODE)." }
Write-Host "Instalador: $target"
