<#
.SYNOPSIS
  Prépare le zip de version à partager : Demenageur-<version>.zip + empreintes SHA-256.
.DESCRIPTION
  Publie l'application (Release, autonome), puis assemble dans artifacts\version :
  - Demenageur-<version>\ : l'exe et ses DLL, LICENSE.txt, THIRD-PARTY-NOTICES.txt, installer.ps1,
    Installer.cmd (double-clic, demande l'UAC) et LISEZMOI.txt ;
  - Demenageur-<version>.zip : ce dossier compressé, à joindre à la Release GitHub ;
  - SHA256SUMS.txt : empreintes du zip et de l'exe, à copier dans la description de la Release.
  Pas besoin des droits administrateur.
.EXAMPLE
  .\scripts\publier-version.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$output = Join-Path $root 'artifacts\version'

function Remove-Tree([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        & cmd.exe /d /c rmdir /s /q $Path
        if (Test-Path -LiteralPath $Path) { throw "Impossible de supprimer « $Path »." }
    }
}

$status = git -C $root status --porcelain
if ($status) { Write-Warning 'Le dépôt contient des modifications non commitées : la version ne correspondra pas exactement au code publié.' }

Write-Host 'Publication de l''application…'
Remove-Tree $publish
dotnet publish (Join-Path $root 'src\Demenageur.App\Demenageur.App.csproj') -p:PublishProfile=win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }

$exe = Join-Path $publish 'Demenageur.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
$name = "Demenageur-$version"
$folder = Join-Path $output $name
$zip = Join-Path $output "$name.zip"

Remove-Tree $output
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Get-ChildItem -LiteralPath $publish -File |
    Where-Object { $_.Extension -in '.exe', '.dll' -or $_.Name -in 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt' } |
    Copy-Item -Destination $folder
Copy-Item (Join-Path $PSScriptRoot 'installer.ps1') $folder
Copy-Item (Join-Path $PSScriptRoot 'version\Installer.cmd') $folder
$readme = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version\LISEZMOI.txt') -Raw -Encoding UTF8).Replace('{VERSION}', $version)
[IO.File]::WriteAllText((Join-Path $folder 'LISEZMOI.txt'), $readme, [Text.UTF8Encoding]::new($true))

Compress-Archive -Path $folder -DestinationPath $zip

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $folder 'Demenageur.exe')).Hash.ToLowerInvariant()
$sums = "$zipHash  $name.zip`r`n$exeHash  Demenageur.exe`r`n"
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Version $version prête dans « $output » :"
Write-Host "  $name.zip   ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) Mo)"
Write-Host '  SHA256SUMS.txt'
Write-Host ''
Write-Host 'À coller dans la description de la Release GitHub :'
Write-Host ''
Write-Host '```'
Write-Host $sums.TrimEnd()
Write-Host '```'
