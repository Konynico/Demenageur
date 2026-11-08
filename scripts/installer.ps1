#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installe (ou désinstalle) Déménageur dans « C:\Program Files\Demenageur », avec un raccourci dans le menu Démarrer.
.DESCRIPTION
  Program Files n'est modifiable que par les administrateurs : aucun programme lancé sans élévation ne peut y
  remplacer un fichier de Déménageur (qui s'exécute, lui, en administrateur).
  Aucune entrée n'est ajoutée au registre. Les données (journaux des opérations) restent dans
  C:\ProgramData\Demenageur et ne sont JAMAIS supprimées par ce script : elles servent à annuler les déplacements.
.EXAMPLE
  .\scripts\installer.ps1
  .\scripts\installer.ps1 -Desinstaller
#>
param([switch]$Desinstaller)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$target = Join-Path $env:ProgramFiles 'Demenageur'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Déménageur.lnk'
# Raccourci mal nommé (« DÃ©mÃ©nageur.lnk ») créé par d'anciennes versions de ce script, enregistrées sans BOM UTF-8 :
# nom construit par codes de caractères pour ne pas dépendre de l'encodage du fichier.
$oldShortcutName = 'D' + [char]0xC3 + [char]0xA9 + 'm' + [char]0xC3 + [char]0xA9 + 'nageur.lnk'
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$oldShortcutName") -ErrorAction SilentlyContinue

if ($Desinstaller) {
    if (Get-Process -Name 'Demenageur' -ErrorAction SilentlyContinue) { throw 'Ferme Déménageur avant de le désinstaller.' }
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $target 'Demenageur.exe')) { Remove-Item $target -Recurse -Force }
    Write-Host 'Déménageur est désinstallé. Les journaux restent dans C:\ProgramData\Demenageur (utiles pour annuler des déplacements).'
    exit 0
}

if (-not (Test-Path (Join-Path $publish 'Demenageur.exe'))) {
    Write-Host 'Publication de l''application…'
    dotnet publish (Join-Path $root 'src\Demenageur.App\Demenageur.App.csproj') -p:PublishProfile=win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
}

if (Get-Process -Name 'Demenageur' -ErrorAction SilentlyContinue) { throw 'Ferme Déménageur avant de le mettre à jour.' }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target 'Demenageur.exe'
$link.WorkingDirectory = $target
$link.Description = 'Déplacer des logiciels installés vers un autre disque'
$link.Save()

Write-Host "Déménageur est installé dans « $target » (raccourci « Déménageur » dans le menu Démarrer)."
