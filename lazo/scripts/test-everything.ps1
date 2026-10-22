$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$test = Join-Path $project 'bin\EverythingSmoke.exe'
$references = foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework')) {
    $match = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory -Filter $name -ErrorAction SilentlyContinue
    } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "$name.dll" -File -Recurse -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (!$match) { throw "No se encontró $name.dll" }
    '/reference:' + $match.FullName
}
& $csc /nologo /target:exe /platform:anycpu /utf8output ("/out:$test") @references (Join-Path $project 'src\EverythingSearch.cs') (Join-Path $project 'tests\EverythingSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la prueba de Everything IPC.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de Everything IPC.' }
