<#
.SYNOPSIS
  Compile la solution Déménageur.
.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'Demenageur.slnx') -c $Configuration --nologo
exit $LASTEXITCODE
