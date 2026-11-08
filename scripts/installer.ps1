#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installe, met à jour ou désinstalle Déménageur dans « C:\Program Files\Demenageur », avec un raccourci dans le menu Démarrer.
.DESCRIPTION
  Program Files n'est modifiable que par les administrateurs : aucun programme lancé sans élévation ne peut y
  remplacer un fichier de Déménageur (qui s'exécute, lui, en administrateur).

  Deux façons de l'utiliser :
  - depuis le dépôt (scripts\installer.ps1) : l'application est REPUBLIÉE à chaque fois (SDK .NET nécessaire),
    on n'installe donc jamais une version périmée ;
  - depuis un dossier de version (Demenageur.exe à côté de ce script) : ces fichiers sont installés tels quels.

  Une mise à jour remplace entièrement l'ancienne version (aucun fichier orphelin). Aucune entrée n'est ajoutée au
  registre : Déménageur n'apparaît donc pas dans « Programmes et fonctionnalités », on le désinstalle avec ce script.

  Les données (C:\ProgramData\Demenageur : journaux des opérations, logs) sont CONSERVÉES à la désinstallation : elles
  servent à annuler les déplacements. -SupprimerDonnees les efface (confirmation demandée).
.EXAMPLE
  .\scripts\installer.ps1
  .\scripts\installer.ps1 -Desinstaller
  .\scripts\installer.ps1 -Desinstaller -SupprimerDonnees
#>
param(
    [switch]$Desinstaller,
    [switch]$SupprimerDonnees
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'Demenageur'
$data = Join-Path $env:ProgramData 'Demenageur'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Déménageur.lnk'
# Raccourci mal nommé (« DÃ©mÃ©nageur.lnk ») créé par d'anciennes versions de ce script, enregistrées sans BOM UTF-8 :
# nom construit par codes de caractères pour ne pas dépendre de l'encodage du fichier.
$oldShortcutName = 'D' + [char]0xC3 + [char]0xA9 + 'm' + [char]0xC3 + [char]0xA9 + 'nageur.lnk'
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$oldShortcutName") -ErrorAction SilentlyContinue

# Suppression d'un dossier SANS jamais suivre de jonction : « rmdir /s » supprime le lien seul, alors que
# Remove-Item -Recurse (PowerShell 5.1) peut descendre dans la cible d'une jonction.
function Remove-Tree([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        & cmd.exe /d /c rmdir /s /q $Path
        if (Test-Path -LiteralPath $Path) { throw "Impossible de supprimer « $Path » (un fichier est peut-être ouvert)." }
    }
}

function Assert-NotRunning([string]$Action) {
    if (Get-Process -Name 'Demenageur' -ErrorAction SilentlyContinue) { throw "Ferme Déménageur avant de le $Action." }
}

if ($SupprimerDonnees -and -not $Desinstaller) { throw '-SupprimerDonnees s''utilise avec -Desinstaller.' }

if ($Desinstaller) {
    Assert-NotRunning 'désinstaller'
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $target 'Demenageur.exe')) { Remove-Tree $target }
    Write-Host 'Déménageur est désinstallé.'

    if (-not $SupprimerDonnees) {
        Write-Host "Les données restent dans « $data » (utiles pour annuler des déplacements). Pour les effacer : -Desinstaller -SupprimerDonnees."
        exit 0
    }

    if (-not (Test-Path -LiteralPath $data)) { Write-Host 'Aucune donnée à effacer.'; exit 0 }
    $operations = @(Get-ChildItem -LiteralPath (Join-Path $data 'operations') -Directory -ErrorAction SilentlyContinue).Count
    Write-Host ''
    Write-Warning "« $data » contient les journaux de $operations opération(s) et les logs techniques."
    Write-Warning 'Si des logiciels sont ENCORE DÉPLACÉS, Déménageur ne pourra plus les annuler après cet effacement'
    Write-Warning '(ils continuent de fonctionner ; la procédure manuelle de LISEZMOI-Demenageur.txt, sur le disque de destination, reste possible).'
    $answer = Read-Host 'Tape SUPPRIMER pour effacer définitivement ces données'
    if ($answer -cne 'SUPPRIMER') { Write-Host 'Données conservées.'; exit 0 }
    Remove-Tree $data
    Write-Host 'Données de Déménageur effacées.'
    exit 0
}

if (Test-Path (Join-Path $PSScriptRoot 'Demenageur.exe')) {
    $source = $PSScriptRoot # dossier de version
}
else {
    $root = Split-Path -Parent $PSScriptRoot
    $source = Join-Path $root 'artifacts\publish\win-x64'
    Write-Host 'Publication de l''application…'
    Remove-Tree $source # repart de zéro : aucun fichier d'une ancienne publication
    dotnet publish (Join-Path $root 'src\Demenageur.App\Demenageur.App.csproj') -p:PublishProfile=win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
}

# Seuls les fichiers de l'application (jamais de .pdb, ni ce script, ni un LISEZMOI de version).
$files = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.exe', '.dll' -or $_.Name -in 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt' })
if (-not ($files | Where-Object Name -eq 'Demenageur.exe')) { throw "Demenageur.exe introuvable dans « $source »." }

Assert-NotRunning 'mettre à jour'
if (Test-Path (Join-Path $target 'Demenageur.exe')) { Remove-Tree $target } # mise à jour : ancienne version retirée entièrement
New-Item -ItemType Directory -Force -Path $target | Out-Null
$files | Copy-Item -Destination $target -Force
# Fichiers d'un zip téléchargé : la marque « provenant d'Internet » est retirée des copies installées (l'utilisateur vient
# de choisir de les installer en administrateur) ; sinon SmartScreen la reverrait à chaque lancement depuis Program Files.
Get-ChildItem -LiteralPath $target -File | Unblock-File

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target 'Demenageur.exe'
$link.WorkingDirectory = $target
$link.Description = 'Déplacer des logiciels installés vers un autre disque'
$link.Save()

$version = (Get-Item (Join-Path $target 'Demenageur.exe')).VersionInfo.ProductVersion.Split('+')[0]
Write-Host "Déménageur $version est installé dans « $target » (raccourci « Déménageur » dans le menu Démarrer)."
