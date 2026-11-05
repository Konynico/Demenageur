namespace Demenageur.Core.FileSystem;

/// <summary>Élément relevé lors du parcours d'une arborescence.</summary>
public sealed record ScannedEntry
{
    /// <summary>Chemin relatif à la racine (« » = la racine), séparateur <c>\</c>, noms exacts (aucune normalisation).</summary>
    public required string RelativePath { get; init; }

    public required FsEntryInfo Info { get; init; }

    /// <summary>Nom court 8.3, s'il diffère du nom long.</summary>
    public string? ShortName { get; init; }

    /// <summary>Contenu du point d'analyse, pour une jonction ou un lien symbolique.</summary>
    public ReparsePointData? Reparse { get; init; }

    /// <summary>Flux de données nommés (ADS).</summary>
    public IReadOnlyList<AlternateStream> Streams { get; init; } = [];

    /// <summary>Descripteur de sécurité (si demandé).</summary>
    public byte[]? SecurityDescriptor { get; init; }

    public FsEntryKind Kind => Info.Kind;

    public int Depth => RelativePath.Length == 0 ? 0 : RelativePath.Count(c => c == '\\') + 1;
}

public enum ScanProblemSeverity
{
    /// <summary>Le déplacement est impossible tant que le problème existe.</summary>
    Blocking,

    /// <summary>Le déplacement reste possible ; l'utilisateur est prévenu.</summary>
    Warning,
}

public enum ScanProblemCode
{
    RootMissing,
    RootIsLink,
    RootNotDirectory,
    Inaccessible,
    Vanished,
    IdentityChanged,
    UnsupportedReparsePoint,
    VolumeMountPoint,
    RelativeLinkEscapes,
    EncryptedFile,
    CaseSensitiveDirectory,
    HardLinkOutside,
    HardLinkInside,
    SparseFile,
    LinkInside,
}

public sealed record ScanProblem(string RelativePath, ScanProblemCode Code, ScanProblemSeverity Severity, string Message);

/// <summary>Résultat du parcours d'une arborescence (dans l'ordre : chaque dossier avant son contenu).</summary>
public sealed class TreeSnapshot
{
    public TreeSnapshot(string rootPath, IReadOnlyList<ScannedEntry> entries, IReadOnlyList<ScanProblem> problems)
    {
        RootPath = rootPath;
        Entries = entries;
        Problems = problems;
    }

    public string RootPath { get; }

    public IReadOnlyList<ScannedEntry> Entries { get; }

    public IReadOnlyList<ScanProblem> Problems { get; }

    public ScannedEntry? Root => Entries.Count > 0 ? Entries[0] : null;

    public bool HasBlockingProblems => Problems.Any(p => p.Severity == ScanProblemSeverity.Blocking);

    public int FileCount => Entries.Count(e => e.Kind == FsEntryKind.File);

    public int DirectoryCount => Entries.Count(e => e.Kind == FsEntryKind.Directory);

    public int LinkCount => Entries.Count(e => e.Kind == FsEntryKind.Link);

    /// <summary>Taille logique totale : flux principaux + flux nommés (un lien physique compte pour chaque nom).</summary>
    public long TotalBytes => Entries.Where(e => e.Kind == FsEntryKind.File).Sum(e => e.Info.Size + e.Streams.Sum(s => s.Size));

    /// <summary>Espace nécessaire sur un volume de clusters de <paramref name="clusterSize"/> octets (arrondi + ~1 Kio de MFT par élément).</summary>
    public long EstimateAllocatedBytes(long clusterSize)
    {
        long total = 0;
        foreach (var entry in Entries)
        {
            total += 1024; // enregistrement MFT
            if (entry.Kind != FsEntryKind.File)
            {
                continue;
            }

            total += RoundUp(entry.Info.Size, clusterSize);
            foreach (var stream in entry.Streams)
            {
                total += RoundUp(stream.Size, clusterSize);
            }
        }

        return total;

        static long RoundUp(long size, long cluster) => cluster <= 0 ? size : (size + cluster - 1) / cluster * cluster;
    }
}
