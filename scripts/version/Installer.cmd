@echo off
rem Double-clic : demande les droits administrateur (UAC), puis installe Déménageur depuis ce dossier.
rem -ExecutionPolicy Bypass ne vaut que pour ce lancement : la politique de Windows n'est pas modifiée.
chcp 65001 >nul
set "SCRIPT=%~dp0installer.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -NoExit -File \"' + $env:SCRIPT + '\"')"
