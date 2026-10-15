$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'NetworkSmoke.exe'
& $csc /nologo /target:exe /utf8output /reference:System.Security.dll /reference:System.Drawing.dll ("/out:" + $exe) `
    (Join-Path $project 'src\SecureChannel.cs') (Join-Path $project 'src\NetworkEngine.cs') (Join-Path $project 'src\Identity.cs') (Join-Path $project 'src\ProfilePhoto.cs') (Join-Path $project 'tests\NetworkSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba de red.' }
$run = Join-Path $output ('smoke-' + [guid]::NewGuid().ToString('N'))
& $exe $run
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de red.' }
