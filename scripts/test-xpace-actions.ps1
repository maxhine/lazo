$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'XpaceActionsSmoke.exe'
& $csc /nologo /target:exe /optimize+ /utf8output /reference:Microsoft.VisualBasic.dll ("/out:" + $exe) (Join-Path $project 'src\XpaceScanner.cs') (Join-Path $project 'src\XpaceActions.cs') (Join-Path $project 'tests\XpaceActionsSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba de acciones de Xpace.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de acciones de Xpace.' }
