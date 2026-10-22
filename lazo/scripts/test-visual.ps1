param([string]$BinDirectory = '')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$bin = if ([string]::IsNullOrWhiteSpace($BinDirectory)) { Join-Path $project 'bin' } elseif ([IO.Path]::IsPathRooted($BinDirectory)) { $BinDirectory } else { Join-Path $project $BinDirectory }
$refs = foreach ($name in @('PresentationCore','PresentationFramework','WindowsBase','System.Xaml')) {
    $dll = Get-ChildItem 'C:/Windows/Microsoft.NET/assembly' -Directory | ForEach-Object {
        Get-ChildItem $_.FullName -Directory -Filter $name
    } | ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter ($name + '.dll') } | Select-Object -First 1
    '/reference:' + $dll.FullName
}
$exe = Join-Path $bin 'VisualSmoke.exe'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' /nologo /target:exe ('/out:' + $exe) $refs (Join-Path $project 'tests/VisualSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba visual.' }
& $exe (Join-Path $bin 'Lazo.exe') (Join-Path $project 'bin/visual-check')
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba visual.' }
