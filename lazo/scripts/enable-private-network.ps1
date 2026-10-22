# Ejecutar como administrador en cada equipo que vaya a recibir archivos.
$ErrorActionPreference = 'Stop'
$ruleName = 'Lazo - red local privada'
$project = Split-Path -Parent $PSScriptRoot
$program = Join-Path $PSScriptRoot 'Lazo.exe'
if (!(Test-Path -LiteralPath $program)) { $program = Join-Path $project 'bin\Lazo.exe' }
if (!(Test-Path -LiteralPath $program)) { throw 'Compila Lazo antes de habilitar el firewall.' }
if (!(Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
        -Program $program -Profile Private -RemoteAddress LocalSubnet `
        -Protocol TCP -LocalPort 48352 | Out-Null
}
if (!(Get-NetFirewallRule -DisplayName "$ruleName - descubrimiento" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "$ruleName - descubrimiento" -Direction Inbound -Action Allow `
        -Program $program -Profile Private -RemoteAddress LocalSubnet `
        -Protocol UDP -LocalPort 48351 | Out-Null
}
Write-Host 'Lazo habilitado para la subred local en redes privadas.'
