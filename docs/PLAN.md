# Plan — « Déménageur » (nom de travail) : déplacer un logiciel installé vers un autre disque, en sécurité

## Contexte

- **C: est presque plein** : 21 Go libres sur 247 Go. D: (167 Go libres, même SSD NVMe que C:) et E: (239 Go libres, second SSD NVMe, volume dynamique) sont en NTFS.
- Objectif : déplacer des logiciels sans les réinstaller et sans toucher au registre. À l'emplacement d'origine, on laisse une **jonction NTFS** : le chemin ne change pas, ni pour Windows ni pour le logiciel.
- Dossier projet vide, pas de dépôt git. Le SDK .NET 10.0.403 et le runtime Windows Desktop 10 sont installés. La protection système (points de restauration) est désactivée sur tous les disques.
- L'outil supprime des fichiers avec les droits administrateur. Toute la conception repose sur un **invariant** : *à tout instant, au moins une copie complète et vérifiée existe*. L'original n'est supprimé qu'après validation de la copie **et** de la jonction.
- Déjà décidé avec toi : .NET 10 LTS ; liste noire à deux niveaux ; l'app n'active jamais elle-même la protection système ; options « Dossier hors registre » et « Redémarrer l'Explorateur ».
- Une revue indépendante des modes de défaillance a été intégrée. Elle a changé l'ordre des étapes (le verrou passe en premier, §3) et ajouté plusieurs protections (§5).

## 1. Stack technique

| Élément | Choix | Pourquoi |
|---|---|---|
| Runtime | C# 14 / **.NET 10 LTS**, `net10.0-windows` | Support jusqu'en nov. 2028 (celui de .NET 8 s'arrête le 10/11/2026) |
| UI | WPF + **WPF-UI** (lepoco, MIT) | Style Fluent Windows 11 : Mica, thème clair/sombre automatique, NavigationView, InfoBar, ContentDialog |
| MVVM / DI | CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting | La logique se teste sans l'UI |
| Copie | API Windows **`CopyFile2`** (P/Invoke) | Le moteur de copie de Windows : données, flux ADS, attributs étendus, attributs. Gère la progression, l'annulation et le mode sans cache |
| Hash | System.IO.Hashing **XxHash128** | Très rapide (le disque reste le goulot) ; détecte toute corruption accidentelle |
| Journal d'opérations | JSON Lines en ajout seul, CRC par enregistrement, écriture directe sur disque | Résiste à une coupure de courant en pleine écriture et garde l'historique |
| Logs | Serilog (fichiers tournants) | Consultables depuis l'app |
| Accès système | System.Management (WMI), P/Invoke `LibraryImport` | Antivirus, point de restauration, jonctions, Restart Manager, privilèges, volumes |
| Tests | xUnit v3, NSubstitute, **BannedApiAnalyzers** | L'analyseur interdit à la compilation tout accès direct au disque ou au registre (§2) |
| Livraison | Publication **autonome** en un seul fichier compressé, installée dans `C:\Program Files\Demenageur` | Une app élevée qui dépend du runtime installé, ou qu'on lance depuis un dossier modifiable, peut être détournée par un utilisateur sans droits (variable `DOTNET_ROOT`, DLL déposées à côté de l'exe) |
| Manifeste | `requireAdministrator`, `longPathAware`, DPI PerMonitorV2, `StartupHookSupport=false` | Élévation UAC au démarrage, chemins de plus de 260 caractères, aucun code injecté au démarrage |

## 2. Architecture

```
E:\Projets\Deplacement fichier\
  Demenageur.sln
  src\Demenageur.Core\        logique pure, sans WPF
     FileSystem\    IFileSystem (seul accès disque), parcours vérifié, copie, vérification, suppression sûre, jonctions
     Moving\        transactions de déplacement et d'annulation, reprise après crash
     Safety\        règles (liste noire), vérifications préalables, planificateur (dry-run)
     Inventory\     registre Uninstall (lecture seule), résolution des dossiers, tailles
     Journal\       journal en ajout seul, manifeste (chemins, identités, tailles, hash)
     Abstractions\  processus, services, antivirus, volumes, point de restauration, Explorateur, alimentation
  src\Demenageur.Windows\     implémentations réelles (Win32, WMI, Restart Manager)
  src\Demenageur.App\         WPF : vues, ViewModels, manifeste, hôte DI
  tests\Demenageur.Tests\     tests unitaires et d'intégration (dossiers factices)
  tests\Demenageur.TestHost\  petite console, tuée volontairement pendant les tests de crash
```

Principes :
1. **On planifie, puis on exécute.** Le dry-run affiche le plan sans rien exécuter : en simulation, l'exécuteur n'est jamais créé. Un test vérifie que les opérations prévues sont exactement celles exécutées.
2. **Transaction journalisée.** Une étape n'est inscrite au journal qu'après le vidage sur disque des données concernées. La reprise se fonde sur l'état réel du disque (noms, identifiants de fichier, cible de la jonction), pas seulement sur le journal.
3. **Un seul accès au disque : `IFileSystem`.** L'analyseur interdit `File`, `Directory` et toute écriture dans le registre partout ailleurs. L'adaptateur registre n'a aucune méthode d'écriture : « aucune modification du registre » est garanti par le compilateur.
4. **Garde de périmètre (`PathGuard`)**, qui enveloppe `IFileSystem`. Pour chaque chemin :
   - il le ramène à sa forme réelle (chemin final, noms 8.3 développés) ;
   - il rejette les formes piégées (`\\?\GLOBALROOT`, UNC, `C:dossier`, `fichier::$DATA`) ;
   - il exige que la cible appartienne au périmètre de l'opération en cours et reste hors des racines protégées.

   En test, il est en plus limité au bac à sable.
5. **Identité vérifiée.** Chaque élément est ouvert sans suivre les points d'analyse (`FILE_FLAG_OPEN_REPARSE_POINT`). Avant toute lecture ou suppression, son identité (numéro de série du volume + FileId) est comparée au manifeste, et la suppression passe par le handle. Remplacer un sous-dossier par une jonction en pleine opération ne peut donc pas détourner l'app vers d'autres fichiers.
6. En test, les services système sont simulés via les interfaces, mais le système de fichiers reste réel.

## 3. Protocole

Notations : **S** = dossier d'origine (`C:\Program Files\App`), **T** = destination (`D:\Logiciels déplacés\App`), **S′** = `S.dmv-old-<id>`, dans le même dossier parent que S.

**Déplacement**

| # | Étape | Si erreur → rollback automatique |
|---|---|---|
| 0 | Vérifications préalables et pré-inventaire (identiques à la simulation) ; point de restauration si accepté | rien n'a changé |
| 1 | Anti-veille et blocage de l'arrêt de Windows. Création de T avec `CreateDirectoryW` (échoue si T existe déjà) et une ACL protégée ; le FileId de T est journalisé | supprimer T |
| 2 | Arrêt de l'Explorateur si accepté. **Renommage S → S′ : c'est le verrou atomique.** Il échoue si un fichier du dossier est ouvert. Dès lors, le logiciel est hors ligne et plus rien ne peut le modifier | supprimer T |
| 3 | Copie S′ → T (`CopyFile2`), puis dates exactes, ACL et propriétaire, liens physiques, jonctions internes, noms 8.3 ; vidage sur disque | supprimer T, renommer S′ → S |
| 4 | **Vérification** par un parcours indépendant de T, comparé au manifeste de S′ : chemins identiques, nombre, tailles, hash de tous les flux, attributs, dates, ACL. Relecture sans cache | idem 3 |
| 5 | Création de S, puis de la jonction S → T, relue aussitôt. Même résultat que `mklink /J`, prouvé par un test. Si un tiers a recréé S entre-temps → arrêt sans rien supprimer, tu décides | supprimer la jonction (le lien seul), renommer S′ → S, supprimer T |
| 6 | Validation à travers la jonction : le contenu vu via S correspond au manifeste. Relance de l'Explorateur | idem 5 |
| — | **Point de non-retour** : le logiciel tourne sur T, vérifié | |
| 7 | Suppression de S′ élément par élément, par handle, avec contrôle d'identité | pas de rollback : on termine (réessais, puis « nettoyage en attente », reproposé au lancement suivant) |
| 8 | Journal « Terminé », mise à jour du `LISEZMOI`, résumé | |

- S′ reste complet jusqu'à l'étape 7, et T est complet et vérifié dès la fin de l'étape 4.
- Le logiciel est indisponible pendant la copie. C'est voulu : rien ne peut changer entre la copie et la suppression de l'original.
- Pas de rollback après l'étape 6 : la copie et la jonction sont alors validées. Revenir en arrière imposerait une recopie complète, plus risquée que de finir le nettoyage.
- Aucune écriture dans le registre, même pour une suppression différée.

**Annuler le déplacement** (T′ = `T.dmv-old-<id>`, S″ = `S.dmv-retour-<id>`)
1. Vérifications :
   - la jonction est bien la nôtre (cible conforme au journal, même FileId pour T) ;
   - aucun processus sur les deux chemins (un programme lancé via la jonction apparaît avec son chemin réel `D:\…`) ;
   - espace sur C: + 10 % ;
   - nouveau manifeste de T, qui a pu être mis à jour depuis le déplacement.
2. Renommage T → T′ (verrou).
3. Copie T′ → S″, vidage sur disque, vérification.
4. Suppression de la jonction (le lien seul).
5. Renommage S″ → S, puis vérification.
6. **Point de non-retour**, puis suppression de T′ par handle.

En cas d'erreur avant l'étape 6 : les renommages sont inversés, la jonction recréée et S″ supprimé.

**Reprise après crash ou coupure de courant**

Elle a lieu au lancement suivant de Déménageur ; l'aide l'explique. Il n'y a pas de démarrage automatique, car il faudrait écrire dans le registre. L'app compare le journal à l'état réel du disque :
- pas encore de jonction → rollback ;
- jonction présente → nouvelle vérification de T, puis fin de l'étape 7 ;
- S est un dossier ordinaire vide → le supprimer, puis rollback ;
- S est un vrai dossier alors que S′ existe aussi → arrêt, tu décides ;
- journal tronqué ou illisible → aucune action automatique, affichage de la procédure manuelle.

La reprise est idempotente : on peut l'interrompre et la relancer sans risque.

## 4. Tes exigences → mise en œuvre

| Exigence | Mise en œuvre |
|---|---|
| Simulation | Toujours exécutée et affichée avant une action réelle : rapport ✓/⚠/✗, étapes, espace, durée. Elle tente aussi d'ouvrir chaque fichier en lecture. Un interrupteur « Simulation uniquement » interdit toute exécution. |
| Liste noire | Règles en JSON (identifiant + raison affichée), appliquées au **dossier** et à toutes les entrées du registre qui pointent dedans ; le niveau le plus strict l'emporte. Les trois niveaux sont détaillés juste après ce tableau. |
| Aucun processus | ① chemin de l'exécutable de chaque processus ; ② **Restart Manager** (processus, services, Explorateur via une extension shell) ; ③ nouvelle vérification juste avant l'étape 2 ; ④ le renommage qui sert de verrou. Si un service est concerné, l'app propose de l'arrêter puis de le relancer, après confirmation. |
| Espace libre | Tailles logiques arrondies aux clusters de la destination, + table MFT, + 10 %. Comparées à l'espace dont dispose l'utilisateur (quotas inclus) et revérifiées avant la copie. Disque plein → rollback. |
| NTFS | Source et destination en NTFS (la jonction est créée sur la source). Destination sur un **disque interne** uniquement : USB, réseau, `subst`, VHD, ReFS/Dev Drive, même volume et chemins imbriqués sont refusés. Le volume réel est résolu par `GetVolumePathNameW`, et le type de bus est lu via les extents du volume (ton E: est un volume dynamique, invisible pour l'API Storage classique). |
| Point de restauration | Proposé et coché par défaut (`SRSetRestorePointW`). Si la protection est désactivée (ton cas), l'app l'explique et propose d'ouvrir « Protection du système ». Elle ne l'active jamais elle-même ; l'activer réserve de l'espace sur C:. Si un point existe déjà dans les 24 h, Windows renvoie « succès » avec l'ancien point : l'app vérifie donc qu'un nouveau point a vraiment été créé. L'aide précise qu'il faut **annuler les déplacements avant de restaurer un point**. |
| Journal + Annuler | Page « Mes déplacements » : état de santé, annulation, contrôle d'intégrité, journal. |
| Rollback automatique | Voir §3 ; le bouton Annuler reste actif jusqu'au point de non-retour. |
| Premier lancement | Avertissement modal sur les risques, case « J'ai compris » obligatoire, proposition d'essayer la démo. |
| Aide intégrée | Voir §6. |
| Droits admin | `requireAdministrator`. |

**Les trois niveaux de la liste noire**
- **Bloqué, sans dérogation** :
  - `C:\Windows` ; les racines (lecteurs, Program Files, ProgramData, Users, profils, dossiers personnels) ; WindowsApps ; Package Cache ; l'app elle-même ;
  - les composants de Windows (Edge, WebView2, Update Health Tools, Defender…) ;
  - les pilotes ;
  - les antivirus (WMI `root\SecurityCenter2` + liste d'éditeurs) ;
  - les **points d'accroche système** qui pointent dans le dossier, sous forme longue, 8.3 ou `\??\` : fournisseurs d'identification, paquets LSA, moniteurs d'impression, Winlogon, IFEO, AppInit_DLLs.
- **Confirmation renforcée** (il faut taper le nom du logiciel) :
  - runtimes partagés (.NET, VC++ Redist, Java, DirectX…) ;
  - logiciels dont dépend un service Windows (`ImagePath` ou `ServiceDll` dans le dossier, ou service signalé par Restart Manager) ;
  - sous-dossiers de Common Files ;
  - entrées masquées (`SystemComponent=1`, cachées par défaut dans la liste).
- **Avertissement** :
  - tâches planifiées, extensions shell ;
  - dossiers voisins du même éditeur (l'app suggère alors de déplacer tout le dossier de l'éditeur) ;
  - entrée HKCU qui pointe hors du profil de son utilisateur.

## 5. Garde-fous supplémentaires

**Intégrité des données**
1. **Les pièges de .NET sont neutralisés.** Par défaut :
   - `EnumerationOptions` ignore les fichiers cachés, système et inaccessibles : la copie *et* la vérification les manqueraient, puis la suppression les effacerait ;
   - l'énumération récursive suit les jonctions ;
   - `Directory.CreateDirectory` réutilise sans prévenir un dossier qui existe déjà ;
   - `File.Move` copie puis supprime en silence quand on change de volume ;
   - `Directory.Delete` s'arrête sur un fichier en lecture seule, après avoir déjà effacé le reste.

   D'où : parcours maison, `CreateDirectoryW`, `MoveFileExW` sans copie implicite, suppression maison.
2. **Copie fidèle** :
   - chemins `\\?\` (noms qui finissent par un point ou une espace, `CON`, plus de 260 caractères) ;
   - noms comparés octet par octet (deux noms Unicode visuellement identiques restent distincts) et enregistrés sans perte dans le journal ;
   - flux ADS, attributs étendus, attributs, noms 8.3 ;
   - dates exactes : sinon, une réparation MSI peut écraser des fichiers de configuration qu'elle croit d'origine.
3. **Points d'analyse à l'intérieur du dossier** :
   - jonctions et liens symboliques absolus : recréés à l'identique ;
   - refusés : liens relatifs qui sortent du dossier, points de montage, AppExecLink, fichiers cloud, types inconnus, fichiers EFS, dossiers sensibles à la casse ;
   - liens physiques : recréés si tous leurs noms sont dans le dossier, sinon refus.

**Sécurité (l'app tourne en administrateur)**
4. **Les ACL sont conservées.** Sans ça, la copie hérite des droits de `D:\`, où les « Utilisateurs authentifiés » peuvent modifier les fichiers. N'importe quel utilisateur pourrait alors remplacer un exécutable ou une DLL qu'un service charge en SYSTEM : ce serait une élévation de privilèges. Concrètement :
   - l'app crée la racine `D:\Logiciels déplacés` avec une ACL protégée (administrateurs et SYSTEM en contrôle total, Utilisateurs en lecture/exécution) ;
   - si cette racine existe déjà avec des droits trop larges ou un autre propriétaire → refus ;
   - ces droits sont revérifiés à chaque lancement.
5. **`C:\ProgramData\Demenageur` est réservé aux administrateurs.** Le propriétaire des journaux est vérifié et chaque chemin relu est revalidé. Un faux journal déposé par un utilisateur ne peut donc rien faire supprimer.
6. **Le registre est lu tel quel, sans expansion automatique des variables.** Elles sont ensuite développées selon la vue 32 ou 64 bits ; sinon, `%ProgramFiles%` dans une entrée 32 bits désignerait le mauvais dossier. Seul le chemin résolu compte, jamais le nom affiché. Les entrées HKCU de chaque profil chargé sont lues dans `HKEY_USERS\<SID>`.
7. **L'app ne lance jamais le logiciel déplacé**, car il tournerait en administrateur.

**Robustesse**
8. **Refus supplémentaires** :
   - source qui est déjà une jonction, ou dont un dossier parent en est une ;
   - déplacements imbriqués ;
   - dossier qui contient d'autres logiciels (la liste est affichée) ;
   - destination qui existe déjà ;
   - renommages en attente de redémarrage sur la source ;
   - destination chiffrée par BitLocker sans déverrouillage automatique.
9. **Une seule instance** (mutex `Global\`). Pendant une opération : pas de mise en veille, arrêt de Windows et fermeture de l'app bloqués, avertissement si l'ordinateur est sur batterie.
10. **Réessais espacés** sur les verrous passagers (antivirus, indexeur).
11. **Bilan de santé des déplacements** : jonction OK ou cassée, dossier recréé par un programme de mise à jour, destination absente, copie orpheline. Une copie orpheline n'est jamais supprimée automatiquement.
12. **`LISEZMOI-Demenageur.txt` à la racine de destination** : liste des déplacements et commandes de restauration manuelle.
13. **Mode « Démo sans risque »** : crée un logiciel fictif (dossier factice + petit script `.cmd` à lancer) dans des dossiers bac à sable, pour essayer déplacement et annulation. Il sert aussi aux tests de bout en bout.

**Options retenues**
- **Dossier hors registre** : bouton « Déplacer un dossier… » pour les jeux et applis portables. Même pipeline et mêmes règles, avec en plus un blocage des dossiers personnels (Documents, Bureau, OneDrive…) et de leurs parents.
- **Redémarrer l'Explorateur** : quand Restart Manager montre que l'Explorateur retient des fichiers (extension du clic droit, ex. 7-Zip), l'app propose de l'arrêter proprement juste avant l'étape 2. Elle le relance après l'étape 6, ou après un rollback. La barre des tâches disparaît le temps de la copie. Si l'app plante entre-temps, elle relance l'Explorateur au démarrage suivant.

## 6. Interface (Fluent Windows 11)

- **Bandeau des disques** : lettre, nom, système de fichiers, type, barre d'occupation (en rouge au-delà de 90 %).
- **Navigation** : Logiciels · Mes déplacements · Journal · Aide · Paramètres.
- **Logiciels** :
  - recherche et filtres (masquer les composants système et les mises à jour, n'afficher que les déplaçables, filtrer par disque) ;
  - liste triable : icône, nom, éditeur, version, dossier, taille réelle (calculée en arrière-plan), disque ;
  - badge : Déplaçable / Confirmation requise / Bloqué / Déjà déplacé / Dossier inconnu ;
  - panneau de détail qui explique chaque règle ;
  - bouton « Déplacer un dossier… ».
- **Assistant de déplacement** :
  1. destination (disques compatibles, espace disponible) ;
  2. simulation (rapport) ;
  3. options et confirmation (point de restauration, niveau de vérification, case à cocher ou saisie du nom) ;
  4. exécution (étapes, progression, débit, temps restant, Annuler jusqu'au point de non-retour) ;
  5. résultat.
- **Aide** :
  - schéma `C:\Program Files\App ⇢ D:\Logiciels déplacés\App` et explication des jonctions ;
  - ce qui continue de fonctionner : raccourcis, registre, désinstallation ;
  - procédure manuelle de secours.
- **Risques expliqués dans l'aide** :
  - disque débranché, lettre de lecteur changée ou disque verrouillé ;
  - **mises à jour** : certains programmes de mise à jour déplacent leurs fichiers depuis `%TEMP%` (sur C:). Vers un autre disque, ce déplacement échoue (`ERROR_NOT_SAME_DEVICE`). D'autres recréent le dossier. En cas d'échec : annuler le déplacement, faire la mise à jour, puis redéplacer ;
  - règles de pare-feu par programme : le processus apparaît avec son chemin réel `D:\…` ;
  - logiciels qui cherchent des dossiers voisins via `..\` ;
  - DRM et anti-triche ;
  - applis UIAccess ou AppLocker qui exigent d'être dans Program Files ;
  - avant une restauration système ou une mise à niveau majeure de Windows, annuler les déplacements.

## 7. Tests (jamais sur un vrai logiciel)

- **Double barrière** :
  - les tests **refusent de tourner en administrateur** : Windows lui-même interdit alors de toucher à Program Files ;
  - `PathGuard` est limité à `%TEMP%\DemenageurTests\<guid>`, et toute sortie de ce dossier fait échouer le test ;
  - un dossier **« canari »**, visé par des jonctions de test, est haché après chaque test.
- **`FakeSoftwareBuilder`** (aléatoire à graine fixe) génère :
  - sous-dossiers imbriqués, fichiers de 0 octet, de 4 Kio ± 1, gros fichiers, fichier creux (sparse), fichier et dossier vides ;
  - fichiers en lecture seule, cachés et système ;
  - noms piégés : `a.`, `b `, `CON`, surrogates isolés, jumeaux Unicode NFC/NFD, espaces, chemins de plus de 260 caractères ;
  - flux ADS, liens physiques, jonctions interne et externe, faux .exe et .dll.
- **Garde** : `..`, `\\?\GLOBALROOT`, UNC, `C:dossier`, `x::$DATA`, alias 8.3 et alias par jonction sont tous rejetés.
- **Copie et vérification** :
  - aller-retour complet, avec un instantané des métadonnées identique avant et après ;
  - altérations détectées : octet modifié, fichier tronqué, supprimé ou ajouté, flux ADS perdu, attribut changé ;
  - sous-dossier inaccessible → arrêt.
- **Refus** : cycle de jonctions, lien relatif qui sort du dossier, lien physique avec un nom à l'extérieur, dossier sensible à la casse, destination existante, jonction, imbriquée ou sur le même volume. Dans chaque cas, rien n'a changé et le canari est intact.
- **Verrou** : un handle ouvert (même avec `FileShare.Delete`) ou un processus enfant dont le dossier courant est dans S → le renommage échoue et rien ne bouge.
- **Pannes injectées, de façon exhaustive** :
  - le N-ième appel qui modifie le disque échoue (`DISK_FULL`, `SHARING_VIOLATION`, `ACCESS_DENIED`, `CRC`), pour chaque valeur de N ;
  - on recommence avec une seconde panne pendant le rollback ;
  - état final attendu : d'origine, terminé, ou bloqué avec au moins une copie vérifiée.
- **Crash réel** :
  - `TestHost` est tué à la N-ième opération, pour chaque valeur de N ;
  - la reprise tourne dans un nouveau processus et est elle-même tuée à chaque étape, pour prouver qu'elle est idempotente ;
  - le journal est tronqué à chaque octet de son dernier enregistrement.
- **Dry-run** : l'instantané reste inchangé, et les opérations prévues sont exactement celles exécutées.
- **Jonction** : ses octets sont identiques à ceux de `mklink /J` (créée dans le bac à sable) ; la supprimer laisse la cible intacte.
- **Annulation** : retour à l'identique, modifications faites depuis le déplacement conservées, pannes injectées.
- **Règles et vérifications préalables** (services simulés) : chemins système, runtimes, service, pilote, points d'accroche, antivirus, seuil exact des 10 %, exFAT/ReFS, USB, même volume.
- **Catégories optionnelles, jamais lancées par défaut** :
  - `CrossVolume` : vrai déplacement de dossiers factices de E: vers `D:\DemenageurTests` ;
  - `Admin` : propriétaire, noms 8.3. Idéalement dans une machine virtuelle, car Windows 11 Famille n'a ni Bac à sable Windows ni Hyper-V.

## 8. Plan d'implémentation par étapes

Chaque étape se termine par une compilation réussie, des tests verts et un court compte rendu.

| Étape | Contenu |
|---|---|
| 0 | Squelette : solution, 5 projets, BannedApiAnalyzers, manifeste admin, hôte DI, Serilog, scripts de build et de test |
| 1 | `IFileSystem`, `PathGuard`, parcours avec contrôle d'identité, classification des points d'analyse, jonctions, suppression sûre, `FakeSoftwareBuilder`, tests du garde et du canari |
| 2 | Copie (`CopyFile2` + métadonnées), manifeste, vérification, tests |
| 3 | **Cœur** : journal, transactions de déplacement et d'annulation, rollback, reprise après crash, tests exhaustifs de pannes et de crash (`TestHost`) |
| 4 | Inventaire : registre HKLM 32/64 bits et `HKEY_USERS\<SID>` ; dossier d'installation (`InstallLocation`, sinon déduit de `DisplayIcon` ou `UninstallString`, à confirmer par toi) ; tailles en arrière-plan ; disques et volumes |
| 5 | Sécurité : règles à deux niveaux, points d'accroche système, inspecteurs (processus, Restart Manager, services et pilotes, antivirus, volumes, BitLocker, renommages en attente), planificateur / dry-run |
| 6 | Système : point de restauration, arrêt et relance de l'Explorateur, anti-veille, blocage de l'arrêt, instance unique |
| 7 | Interface : fenêtre Fluent, bandeau des disques, liste, détail, dossier hors registre, assistant, progression, Mes déplacements, journal, paramètres |
| 8 | Premier lancement, aide, mode démo |
| 9 | Durcissement : tests `CrossVolume`, parcours manuel complet en mode démo, publication autonome, script d'installation dans Program Files |

## 9. Vérification de bout en bout

- `dotnet build` puis `dotnet test` à chaque étape, sans droits admin.
- Tests entre volumes : `$env:DEMENAGEUR_TEST_DEST='D:\DemenageurTests'; dotnet test --filter Category=CrossVolume`.
- App en **mode démo** (tu valides l'invite UAC) :
  1. création d'un logiciel fictif sur E:, simulation, déplacement vers D: ;
  2. contrôle de la jonction (`dir /AL`, `fsutil reparsepoint query`), puis tu lances toi-même le script fictif à travers elle ;
  3. annulation, puis contrôle de la restauration ;
  4. panne injectée (option de débogage) pour observer le rollback automatique.
- Aucun vrai logiciel n'est déplacé pendant le développement. Le premier déplacement réel reste ta décision, après lecture de la simulation.

## Choix de conception à valider

- **Le verrou d'abord** : l'original est renommé *avant* la copie. Le logiciel est indisponible pendant la copie, mais aucune modification ne peut se perdre. Après une coupure, c'est en relançant Déménageur que le logiciel est remis en état.
- Pas de rollback après le point de non-retour (étape 6) : on termine le nettoyage.
- La jonction est créée par l'API Win32 ; un test prouve qu'elle est identique à celle de `mklink /J`.
- Dépôt **git local** (rien n'est envoyé en ligne), avec un commit à la fin de chaque étape validée. Dis-le si tu n'en veux pas.
- Nom de travail « Déménageur » ; dossier cible par défaut `<disque>:\Logiciels déplacés\<nom d'origine>` ; app installée dans `C:\Program Files\Demenageur`.
