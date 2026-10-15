$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'PointCloudSmoke.exe'
& $csc /nologo /target:exe /optimize+ /utf8output ("/out:" + $exe) /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $project 'src\PointCloudConverter.cs') (Join-Path $project 'src\CoordinateSystems.cs') (Join-Path $project 'src\Geodesy.cs') (Join-Path $project 'src\CloudReprojector.cs') (Join-Path $project 'tests\PointCloudSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba de nubes de puntos.' }
& $exe (Join-Path $output 'pointcloud-check')
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de nubes de puntos.' }
