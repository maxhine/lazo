param([string]$BinDirectory = '')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$bin = if ([string]::IsNullOrWhiteSpace($BinDirectory)) { Join-Path $project 'bin' } elseif ([IO.Path]::IsPathRooted($BinDirectory)) { $BinDirectory } else { Join-Path $project $BinDirectory }
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$test = Join-Path $project 'bin\InstallerSmoke.exe'
& $csc /nologo /target:exe /utf8output ("/out:" + $test) (Join-Path $project 'tests\InstallerSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba del instalador.' }
$installer = Join-Path $project 'dist\Lazo-Setup-0.4.16.exe'
$application = Join-Path $bin 'Lazo.exe'
if (!(Test-Path -LiteralPath $installer)) { throw 'No existe el instalador 0.4.16; créalo antes de probarlo.' }
if (!(Test-Path -LiteralPath $application)) { throw 'No existe el binario de Lazo; compila antes de probar el instalador.' }
& $test $installer $application
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba del instalador.' }
