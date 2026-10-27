# Publica la versión optimizada de la isla en una carpeta fija y la abre.
# El inicio con Windows se actualiza solo para apuntar a esta carpeta.
$ErrorActionPreference = 'Stop'

$destino = Join-Path $env:LOCALAPPDATA 'Programs\IslaDinamica'
$proyecto = $PSScriptRoot

Write-Host "Cerrando la isla si esta abierta..."
Get-Process IslaDinamica -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 800

Write-Host "Publicando en $destino ..."
& dotnet publish $proyecto -c Release -o $destino -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación" }

Start-Process (Join-Path $destino 'IslaDinamica.exe')
Write-Host "Listo: la isla quedo instalada y abierta."
