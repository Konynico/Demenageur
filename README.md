# Déménageur

Application Windows (WPF, .NET 10) qui déplace un logiciel installé vers un autre disque, en sécurité :
copie, vérification complète, puis jonction NTFS à l'emplacement d'origine (comme `mklink /J`).
Windows et le logiciel continuent d'utiliser le même chemin. **Aucune modification du registre.**

Le plan détaillé (architecture, protocole, garde-fous, tests) est dans [docs/PLAN.md](docs/PLAN.md).

## Installer et lancer

```powershell
# Dans un terminal PowerShell lancé EN ADMINISTRATEUR :
.\scripts\installer.ps1              # republie, remplace la version installée dans C:\Program Files\Demenageur, raccourci menu Démarrer
.\scripts\installer.ps1 -Desinstaller                     # garde les journaux (utiles pour annuler des déplacements)
.\scripts\installer.ps1 -Desinstaller -SupprimerDonnees   # efface aussi C:\ProgramData\Demenageur (confirmation demandée)
```

Aucune entrée n'est ajoutée au registre : Déménageur n'apparaît pas dans « Programmes et fonctionnalités ».
Placé à côté de `Demenageur.exe` (dossier de version), `installer.ps1` installe ces fichiers sans republier.

L'application demande les droits administrateur (UAC) à chaque lancement. Elle doit tourner depuis Program Files :
lancée depuis un dossier modifiable sans élévation, elle affiche un avertissement au démarrage.

**Premiers pas conseillés :**
1. Lis l'avertissement du premier lancement.
2. Essaie la démo sans risque : un logiciel fictif déplacé d'un disque à l'autre, puis l'annulation.
3. Active si tu veux « Simulation uniquement » (Paramètres) pour tout explorer sans risque.

## Fonctionnement

1. **Simulation**, en lecture seule. Elle vérifie :
   - la liste noire ;
   - les processus et services (Restart Manager) ;
   - l'espace (taille + 10 %) ;
   - que le disque cible est interne et en NTFS ;
   - que chaque fichier s'ouvre en lecture.

   Elle liste ensuite les étapes.
2. **Verrou** : le dossier d'origine est renommé. L'opération échoue si un fichier est ouvert, et garantit que rien ne change pendant la copie.
3. **Copie** (CopyFile2), puis **vérification indépendante** : relecture sans cache, XxHash128 de chaque flux, dates, attributs, liens et permissions.
4. **Jonction**, puis validation à travers elle. C'est le **point de non-retour**.
5. **Suppression de l'original**, élément par élément, d'après l'inventaire et l'identité de chaque fichier.

Toute erreur avant le point de non-retour ramène automatiquement à l'état initial. Après un crash ou une coupure de
courant, l'opération est reprise au lancement suivant. « Mes déplacements » permet d'annuler un déplacement : la copie
est rapatriée à l'emplacement d'origine, avec les mêmes vérifications.

**Jeux des lanceurs.** Déménageur lit, en lecture seule, ce que chaque lanceur publie :
- **Steam** : `libraryfolders.vdf` ;
- **Epic Games** : ses manifestes ;
- **GOG Galaxy** et **Ubisoft Connect** : le registre ;
- **application Xbox** : le fichier `.GamingRoot` ;
- **Battle.net, EA, Riot et Rockstar** : leur désinstalleur.

Un jeu géré par Steam ou par l'application Xbox, qui savent le déplacer eux-mêmes, passe au statut « Via son lanceur » ; le déplacer avec Déménageur demande alors de taper son nom. Les autres lanceurs déclenchent un simple avertissement, avec leur procédure. Les dossiers exclus à la main (bouton « Exclure » ou Paramètres) sont bloqués.

**Désinstallation et mises à jour.** Un désinstalleur supprime les fichiers qu'il connaît à travers la jonction, puis
souvent la jonction elle-même. Certaines mises à jour, elles, recréent un vrai dossier à l'origine. Dans les deux cas,
une copie orpheline reste sur le disque cible. Elle est signalée au démarrage et dans « Mes déplacements »
(« Désinstallé » ou « Remplacé par un dossier »).

« Nettoyer la copie… » la supprime. C'est une opération journalisée et reprise après un crash, comme les autres :
1. simulation ;
2. contrôle d'identité ;
3. verrou ;
4. suppression élément par élément, sans suivre les liens.

Ce qui se trouve à l'emplacement d'origine n'est jamais touché, avec une seule exception : la jonction laissée en place
par un désinstalleur. Elle n'est supprimée qu'après saisie du nom du logiciel, et seulement si plus aucune entrée de
désinstallation ne désigne ce dossier.

## Confidentialité

Déménageur n'envoie rien sur Internet et ne collecte rien. Il enregistre uniquement sur le PC, dans
`C:\ProgramData\Demenageur` (réservé aux administrateurs) :
- les journaux de ses opérations (chemins, identifiants de fichiers, empreintes, permissions), nécessaires pour annuler un déplacement ;
- des logs techniques, conservés 60 jours.

Le fichier `LISEZMOI-Demenageur.txt` du dossier de destination liste les logiciels déplacés (lisible par les
administrateurs et le compte qui les a déplacés). Pour effacer : Paramètres → « Effacer l'historique des opérations
terminées… » (les journaux des logiciels encore déplacés sont conservés), ou `installer.ps1 -Desinstaller -SupprimerDonnees`.
Ces fichiers ne sont pas chiffrés par Déménageur : BitLocker protège le disque si besoin.

## Licence

Déménageur est distribué sous licence [MIT](LICENSE) : tu peux l'utiliser, le modifier et le redistribuer librement,
y compris l'exécutable, à condition de conserver l'avis de licence. Il est fourni « tel quel », **sans aucune garantie** :
l'auteur n'est pas responsable des conséquences de son utilisation.

## Composants tiers

WPF-UI et CommunityToolkit.Mvvm (MIT), Serilog (Apache-2.0), .NET et Microsoft.Extensions (MIT). Les licences complètes
sont dans [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), livré à côté de l'exécutable.

## Structure

| Projet | Rôle |
|---|---|
| `src/Demenageur.Core` | Logique pure : moteur de déplacement, vérification, journal, règles, simulation. Aucun accès direct au disque. |
| `src/Demenageur.Windows` | Implémentations Windows : Win32, WMI, Restart Manager, registre en lecture seule. |
| `src/Demenageur.App` | Interface WPF (WPF-UI, Fluent), lancée en administrateur. |
| `tests/Demenageur.Tests` | Tests sur dossiers factices uniquement. |
| `tests/Demenageur.TestHost` | Console tuée volontairement pendant les tests de crash. |

## Compiler et tester

```powershell
.\scripts\build.ps1
.\scripts\test.ps1            # tout, y compris les tests exhaustifs (quelques minutes) — terminal NON administrateur
.\scripts\test.ps1 -Rapide    # sans les tests exhaustifs (environ 20 s)
.\scripts\test.ps1 -CrossVolume -CrossVolumeDestination 'D:\DemenageurTests'   # vrais déplacements entre deux volumes
```

Les tests exhaustifs couvrent :
- une panne injectée à **chaque** opération disque (avant et après), et une double panne pendant le rollback ;
- les pannes pendant l'annulation ;
- de **vrais crashs de processus** à chaque opération, puis pendant la reprise ;
- un journal tronqué à chaque octet.

**Version « aperçu »** (développement de l'interface) : `dotnet build src\Demenageur.App -p:DemenageurApercu=true`.
Elle tourne sans élévation, en simulation uniquement, et range ses données dans `%TEMP%`.

## Règles de sécurité du code

- Le registre n'est jamais modifié : les API d'écriture sont interdites à la compilation (`build/BannedSymbols.Registry.txt`).
- `Core`, `App` et `TestHost` n'accèdent jamais directement au disque. Tout passe par `IFileSystem`, enveloppé
  par une garde de périmètre (`build/BannedSymbols.FileSystem.txt`).
- Les tests ne touchent que des dossiers factices sous `%TEMP%\DemenageurTests` (ou le dossier `DemenageurTests`
  indiqué pour les tests entre volumes). Ils refusent de tourner en administrateur.

## Limites connues

- Le drapeau de contrôle « DACL auto-héritée » (AI) n'est pas recopié, sans effet sur les droits accordés. La SACL
  (audit) n'est pas copiée.
- Les noms courts 8.3 ne sont reposés qu'avec le privilège de restauration (application lancée en administrateur).
- Refusés : fichiers chiffrés EFS, fichiers cloud, dossiers sensibles à la casse, liens physiques vers l'extérieur,
  points de montage de volume.
