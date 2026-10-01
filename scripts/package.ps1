param([switch]$SkipBuild,[string]$BinDirectory = '')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$bin = if ([string]::IsNullOrWhiteSpace($BinDirectory)) { Join-Path $project 'bin' } elseif ([IO.Path]::IsPathRooted($BinDirectory)) { $BinDirectory } else { Join-Path $project $BinDirectory }
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Release -OutputDirectory $bin
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar Lazo.' }
}
$dist = Join-Path $project 'dist'
$folder = Join-Path $dist 'Lazo-0.4.15'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$application = Join-Path $bin 'Lazo.exe'
if (!(Test-Path -LiteralPath $application)) { throw 'No existe el binario de Lazo; compila antes de empaquetar.' }
Copy-Item -LiteralPath $application -Destination (Join-Path $folder 'Lazo.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'enable-private-network.ps1') -Destination (Join-Path $folder 'enable-private-network.ps1') -Force
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination (Join-Path $folder 'README.md') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'docs\interfaz-oscura.png') -Destination (Join-Path $folder 'docs\interfaz-oscura.png') -Force
Copy-Item -LiteralPath (Join-Path $project 'docs\interfaz-colapsada.png') -Destination (Join-Path $folder 'docs\interfaz-colapsada.png') -Force
Copy-Item -LiteralPath (Join-Path $project 'docs\interfaz-standard.png') -Destination (Join-Path $folder 'docs\interfaz-standard.png') -Force
Copy-Item -LiteralPath (Join-Path $project 'docs\vidrio.png') -Destination (Join-Path $folder 'docs\vidrio.png') -Force
$zip = Join-Path $dist 'Lazo-0.4.15.zip'
Compress-Archive -LiteralPath $folder -DestinationPath $zip -Force
Write-Host "Paquete: $zip"
