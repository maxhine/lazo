$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = if (Test-Path -LiteralPath 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe') {
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
} else { 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$output = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'ChatSmoke.exe'
# Sin interfaz: no abre ninguna ventana y usa una carpeta temporal para los datos del chat.
$gac = 'C:\Windows\Microsoft.NET\assembly'
$references = foreach ($name in @('PresentationCore','PresentationFramework','WindowsBase','System.Xaml')) {
    $dll = Get-ChildItem -LiteralPath $gac -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $name -ErrorAction SilentlyContinue
    } | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter "$name.dll" -File -Recurse -ErrorAction SilentlyContinue } | Select-Object -First 1
    '/reference:' + $dll.FullName
}
$sources = Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'Program.cs' } | Select-Object -ExpandProperty FullName
& $csc /nologo /target:exe /platform:anycpu /utf8output ('/out:' + $exe) /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $references $sources (Join-Path $project 'tests\ChatSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba de chat.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de chat.' }
