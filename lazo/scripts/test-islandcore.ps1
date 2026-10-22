# El camino de vuelta a la isla: dónde la busca el Lazo clásico (sin abrir nada).
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') { 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' } else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'IslandCoreSmoke.exe'
& $csc /nologo /target:exe /optimize+ /utf8output ("/out:" + $exe) (Join-Path $project 'src\IslandCore.cs') (Join-Path $project 'tests\IslandCoreSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba del camino de vuelta a la isla.' }
