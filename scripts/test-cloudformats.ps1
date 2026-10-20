$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$gac = 'C:\Windows\Microsoft.NET\assembly'
$references = foreach ($name in @('PresentationCore','WindowsBase','System.Xaml')) {
    $match = Get-ChildItem -LiteralPath $gac -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $name -ErrorAction SilentlyContinue
    } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "$name.dll" -File -Recurse -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    '/reference:' + $match.FullName
}
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'CloudFormatsSmoke.exe'
$src = 'PointCloudConverter','CoordinateSystems','Geodesy','CloudReprojector','CloudReaders','CloudExport','CloudPreview' | ForEach-Object { Join-Path $project "src\$_.cs" }
& $csc /nologo /target:exe /optimize+ /utf8output ("/out:" + $exe) /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $references $src (Join-Path $project 'tests\CloudFormatsSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba de formatos de nube.' }
& $exe (Join-Path $output 'cloudformats-check')
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de formatos de nube.' }
