param([switch]$Release,[string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$source = Join-Path $project 'src'
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $project 'bin'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $project $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.8 es necesario para compilar Lazo.' }

$gac = 'C:\Windows\Microsoft.NET\assembly'
$assemblies = @('PresentationCore','PresentationFramework','WindowsBase','System.Xaml')
$references = foreach ($name in $assemblies) {
    $match = Get-ChildItem -LiteralPath $gac -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $name -ErrorAction SilentlyContinue
    } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "$name.dll" -File -Recurse -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (!$match) { throw "No se encontró $name.dll" }
    '/reference:' + $match.FullName
}

$sources = Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/utf8output','/warn:4',('/out:' + (Join-Path $output 'Lazo.exe')))
$icon = Join-Path $project 'assets\lazo.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon }
if ($Release) { $arguments += '/optimize+' } else { $arguments += '/debug+' }
$arguments += $references
$arguments += $sources
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación (código $LASTEXITCODE)." }
Write-Host "Compilado: $(Join-Path $output 'Lazo.exe')"
