<#
.SYNOPSIS
  Lance les tests de Déménageur (toujours depuis un terminal NON administrateur).
.DESCRIPTION
  Par défaut : tous les tests sur dossiers factices du dossier temporaire, y compris les tests exhaustifs
  (panne et crash à chaque opération disque : quelques minutes).
  -Rapide       : sans les tests exhaustifs.
  -CrossVolume  : vrais déplacements de dossiers factices entre deux volumes, vers -CrossVolumeDestination.
.EXAMPLE
  .\scripts\test.ps1
  .\scripts\test.ps1 -Rapide
  .\scripts\test.ps1 -CrossVolume -CrossVolumeDestination 'D:\DemenageurTests'
#>
param(
    [switch]$Rapide,
    [switch]$CrossVolume,
    [string]$CrossVolumeDestination = 'D:\DemenageurTests'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Les tests refusent de tourner en administrateur : relance depuis un terminal non élevé.'
    exit 1
}

dotnet build (Join-Path $root 'Demenageur.slnx') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root 'tests\Demenageur.Tests\bin\Debug\net10.0-windows\Demenageur.Tests.exe'
$arguments = @()
if ($CrossVolume) {
    $env:DEMENAGEUR_TEST_DEST = $CrossVolumeDestination
    $arguments += @('-trait', 'Category=CrossVolume')
}
else {
    Remove-Item Env:\DEMENAGEUR_TEST_DEST -ErrorAction SilentlyContinue
    $arguments += @('-notrait', 'Category=CrossVolume', '-notrait', 'Category=Admin')
    if ($Rapide) { $arguments += @('-notrait', 'Category=Exhaustif') }
}

& $exe @arguments
exit $LASTEXITCODE
