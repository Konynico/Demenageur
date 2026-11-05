namespace Demenageur.Core.FileSystem;

/// <summary>Horodatages exacts au format FILETIME (intervalles de 100 ns depuis 1601, UTC). 0 = inconnu / ne pas modifier.</summary>
public readonly record struct FileTimestamps(long CreationTime, long LastAccessTime, long LastWriteTime)
{
    public static DateTime ToDateTimeUtc(long fileTime)
        => fileTime <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(fileTime);
}

/// <summary>Nature d'un élément, déduite de ses attributs et de son point d'analyse.</summary>
public enum FsEntryKind
{
    /// <summary>Fichier ordinaire (y compris compressé WOF ou dédupliqué : données transparentes).</summary>
    File,

    /// <summary>Dossier ordinaire.</summary>
    Directory,

    /// <summary>Jonction ou lien symbolique (point d'analyse « nom de substitution »).</summary>
    Link,

    /// <summary>Point d'analyse non pris en charge (cloud, AppExecLink, type inconnu…).</summary>
    UnsupportedReparsePoint,
}

/// <summary>Informations sur un élément, lues sans suivre de point d'analyse final.</summary>
public sealed record FsEntryInfo
{
    /// <summary>Chemin complet tel que demandé.</summary>
    public required string Path { get; init; }

    public required FileAttributes Attributes { get; init; }

    /// <summary>Étiquette du point d'analyse (0 si l'élément n'en est pas un).</summary>
    public required uint ReparseTag { get; init; }

    /// <summary>Taille logique du flux principal (0 pour un dossier).</summary>
    public required long Size { get; init; }

    public required FileTimestamps Times { get; init; }

    public required FileIdentity Identity { get; init; }

    /// <summary>Nombre de noms (liens physiques) du fichier.</summary>
    public required uint LinkCount { get; init; }

    /// <summary>Dossier sensible à la casse (fonctionnalité WSL).</summary>
    public required bool IsCaseSensitiveDirectory { get; init; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public FsEntryKind Kind => Classify(Attributes, ReparseTag);

    public static FsEntryKind Classify(FileAttributes attributes, uint reparseTag)
    {
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return isDirectory ? FsEntryKind.Directory : FsEntryKind.File;
        }

        if (ReparseTags.IsNameSurrogate(reparseTag))
        {
            return reparseTag is ReparseTags.MountPoint or ReparseTags.Symlink
                ? FsEntryKind.Link
                : FsEntryKind.UnsupportedReparsePoint;
        }

        // Données transparentes : la lecture normale renvoie le contenu réel (compression WOF, déduplication).
        if (!isDirectory && ReparseTags.IsDataTransparent(reparseTag))
        {
            return FsEntryKind.File;
        }

        return FsEntryKind.UnsupportedReparsePoint;
    }
}

/// <summary>Entrée renvoyée par l'énumération d'un dossier.</summary>
public sealed record DirectoryChild(string Name, ulong FileId64, FileAttributes Attributes, string? ShortName);

/// <summary>Flux de données nommé (ADS) d'un fichier.</summary>
public sealed record AlternateStream(string Name, long Size);

/// <summary>Empreinte XxHash128 d'un flux de données.</summary>
public readonly record struct ContentHash(UInt128 Value)
{
    public override string ToString() => Value.ToString("X32", System.Globalization.CultureInfo.InvariantCulture);

    public static ContentHash Parse(string text)
        => new(UInt128.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Parties du descripteur de sécurité à lire ou écrire.</summary>
[Flags]
public enum SecurityParts
{
    None = 0,
    Owner = 1,
    Group = 2,
    Dacl = 4,
    All = Owner | Group | Dacl,
}

/// <summary>Type de lecteur, au sens de Windows.</summary>
public enum VolumeDriveType
{
    Unknown,
    NoRootDirectory,
    Removable,
    Fixed,
    Remote,
    CdRom,
    RamDisk,
}

/// <summary>Informations sur le volume qui contient un chemin.</summary>
public sealed record VolumeInfo
{
    /// <summary>Racine du volume, par exemple <c>C:\</c>.</summary>
    public required string RootPath { get; init; }

    /// <summary>Chemin GUID du volume (<c>\\?\Volume{…}\</c>), identité stable du volume.</summary>
    public required string? VolumeGuidPath { get; init; }

    public required string FileSystemName { get; init; }

    public required string Label { get; init; }

    public required uint FileSystemFlags { get; init; }

    public required long ClusterSize { get; init; }

    /// <summary>Espace libre utilisable par l'utilisateur courant (quotas inclus).</summary>
    public required long FreeBytesAvailable { get; init; }

    public required long TotalBytes { get; init; }

    public required VolumeDriveType DriveType { get; init; }

    public bool IsNtfs => string.Equals(FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Options de copie d'un fichier.</summary>
[Flags]
public enum CopyFileOptions
{
    None = 0,

    /// <summary>Copie sans cache système (gros fichiers) : les données vont directement sur le disque.</summary>
    NoBuffering = 1,
}
