# Déménageur

Application Windows (WPF, .NET 10) qui déplace un logiciel installé vers un autre disque en toute sécurité :
copie → vérification complète → jonction NTFS à l'emplacement d'origine. Aucune modification du registre.

Le plan détaillé (architecture, protocole, garde-fous, tests) est dans [docs/PLAN.md](docs/PLAN.md).

## Structure

| Projet | Rôle |
|---|---|
| `src/Demenageur.Core` | Logique pure : moteur de déplacement, vérification, journal, règles. Aucun accès direct au disque. |
| `src/Demenageur.Windows` | Implémentations Windows : Win32, WMI, Restart Manager, registre en lecture seule. |
| `src/Demenageur.App` | Interface WPF (Fluent), lancée en administrateur. |
| `tests/Demenageur.Tests` | Tests sur dossiers factices uniquement. |
| `tests/Demenageur.TestHost` | Console tuée volontairement pendant les tests de crash. |

## Compiler et tester

```powershell
.\scripts\build.ps1
.\scripts\test.ps1          # depuis un terminal NON administrateur
```

## Règles de sécurité du code

- Le registre n'est jamais modifié : les API d'écriture sont interdites à la compilation (`build/BannedSymbols.Registry.txt`).
- `Core`, `App` et `TestHost` n'accèdent jamais directement au disque : tout passe par `IFileSystem`
  (`build/BannedSymbols.FileSystem.txt`).
- Les tests ne touchent que des dossiers factices sous `%TEMP%\DemenageurTests` et refusent de tourner en administrateur.
