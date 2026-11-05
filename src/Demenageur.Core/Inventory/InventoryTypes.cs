namespace Demenageur.Core.Inventory;

/// <summary>Où l'entrée de désinstallation a été trouvée.</summary>
public enum RegistryScope
{
    /// <summary>HKLM, vue 64 bits.</summary>
    Machine64,

    /// <summary>HKLM, vue 32 bits (WOW6432Node).</summary>
    Machine32,

    /// <summary>Profil d'un utilisateur (HKEY_USERS\SID).</summary>
    User,
}

/// <summary>Entrée brute du registre Uninstall, valeurs déjà développées selon la vue et le profil concernés.</summary>
public sealed record UninstallEntry
{
    /// <summary>Chemin complet de la clé (pour information et dédoublonnage).</summary>
    public required string RegistryKey { get; init; }

    public required RegistryScope Scope { get; init; }

    /// <summary>SID du profil (entrées HKEY_USERS).</summary>
    public string? UserSid { get; init; }

    /// <summary>Dossier de profil de cet utilisateur (entrées HKEY_USERS).</summary>
    public string? UserProfile { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? DisplayVersion { get; init; }

    public string? InstallLocation { get; init; }

    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }

    /// <summary>Taille estimée déclarée par l'installateur, en Kio (souvent fausse ou absente).</summary>
    public long? EstimatedSizeKb { get; init; }

    /// <summary>SystemComponent = 1 : entrée masquée de « Programmes et fonctionnalités ».</summary>
    public bool SystemComponent { get; init; }

    /// <summary>Mise à jour ou correctif d'un autre produit (ParentKeyName, ReleaseType…).</summary>
    public bool IsUpdate { get; init; }

    public bool WindowsInstaller { get; init; }

    public string? InstallDate { get; init; }
}

/// <summary>Source de la résolution du dossier d'installation.</summary>
public enum FolderSource
{
    /// <summary>Aucun dossier trouvé.</summary>
    None,

    /// <summary>Valeur InstallLocation du registre (fiable).</summary>
    InstallLocation,

    /// <summary>Déduit de l'icône (DisplayIcon) : à confirmer.</summary>
    DisplayIcon,

    /// <summary>Déduit de la commande de désinstallation : à confirmer.</summary>
    UninstallString,

    /// <summary>Choisi par l'utilisateur.</summary>
    Manual,
}

/// <summary>Dossier d'installation retenu pour une entrée.</summary>
public sealed record ResolvedFolder(string? Path, FolderSource Source)
{
    public static ResolvedFolder NotFound { get; } = new(null, FolderSource.None);

    /// <summary>Vrai si le dossier vient d'une déduction et doit être confirmé par l'utilisateur.</summary>
    public bool NeedsConfirmation => Source is FolderSource.DisplayIcon or FolderSource.UninstallString;
}

/// <summary>Logiciel installé, tel que présenté à l'utilisateur.</summary>
public sealed record InstalledSoftware
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public required ResolvedFolder Folder { get; init; }

    public required IReadOnlyList<UninstallEntry> Entries { get; init; }

    public string? IconPath { get; init; }

    public long? EstimatedSizeKb { get; init; }

    /// <summary>Toutes les entrées sont masquées (SystemComponent).</summary>
    public bool IsHidden => Entries.All(e => e.SystemComponent);

    public RegistryScope Scope => Entries[0].Scope;
}

/// <summary>Chemins système connus (fournis par Demenageur.Windows).</summary>
public interface IKnownPaths
{
    string WindowsDirectory { get; }

    string SystemDrive { get; }

    string ProgramFiles { get; }

    string ProgramFilesX86 { get; }

    string CommonProgramFiles { get; }

    string CommonProgramFilesX86 { get; }

    string ProgramData { get; }

    string UsersDirectory { get; }

    /// <summary>Dossiers de profil des utilisateurs (C:\Users\xxx).</summary>
    IReadOnlyList<string> UserProfiles { get; }

    /// <summary>Dossiers personnels connus de chaque profil (Documents, Bureau, Images, OneDrive…).</summary>
    IReadOnlyList<string> PersonalFolders { get; }

    /// <summary>Dossier de l'application elle-même.</summary>
    string ApplicationDirectory { get; }

    /// <summary>Dossier de données de l'application.</summary>
    string ApplicationDataDirectory { get; }
}

/// <summary>Lecture (seule) des entrées de désinstallation.</summary>
public interface IUninstallSource
{
    IReadOnlyList<UninstallEntry> ReadAll();
}
