$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$test = Join-Path $project 'bin\InstallerSmoke.exe'
& $csc /nologo /target:exe /utf8output ("/out:" + $test) (Join-Path $project 'tests\InstallerSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba del instalador.' }
& $test (Join-Path $project 'dist\Lazo-Setup-0.4.14.exe') (Join-Path $project 'bin\Lazo.exe')
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba del instalador.' }
