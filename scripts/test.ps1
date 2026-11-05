<#
.SYNOPSIS
  Lance les tests de Déménageur (toujours depuis un terminal NON administrateur).
.DESCRIPTION
  Par défaut, seuls les tests sur dossiers factices du dossier temporaire sont lancés.
  -CrossVolume ajoute les vrais déplacements entre volumes (dossiers factices uniquement)
  vers le dossier indiqué par -CrossVolumeDestination.
.EXAMPLE
  .\scripts\test.ps1
  .\scripts\test.ps1 -CrossVolume -CrossVolumeDestination 'D:\DemenageurTests'
#>
param(
    [switch]$CrossVolume,
    [string]$CrossVolumeDestination = 'D:\DemenageurTests'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Demenageur.slnx'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Les tests refusent de tourner en administrateur : relance depuis un terminal non élevé.'
    exit 1
}

if ($CrossVolume) {
    $env:DEMENAGEUR_TEST_DEST = $CrossVolumeDestination
    dotnet test $solution --nologo --filter 'Category=CrossVolume'
}
else {
    Remove-Item Env:\DEMENAGEUR_TEST_DEST -ErrorAction SilentlyContinue
    dotnet test $solution --nologo --filter 'Category!=CrossVolume&Category!=Admin'
}
exit $LASTEXITCODE
