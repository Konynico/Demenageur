using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Copy;

/// <summary>Flux de données nommé d'un fichier du manifeste.</summary>
public sealed class ManifestStream
{
    public required string Name { get; init; }

    public required long Size { get; init; }

    public ContentHash? Hash { get; set; }
}

/// <summary>Élément du manifeste : tout ce qu'il faut pour recréer puis vérifier une copie à l'identique.</summary>
public sealed class ManifestEntry
{
    /// <summary>Attributs recopiés et comparés (les autres sont gérés par le système : répertoire, point d'analyse, compression…).</summary>
    public const FileAttributes CopiedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    public required string RelativePath { get; init; }

    public required FsEntryKind Kind { get; init; }

    public required FileAttributes Attributes { get; init; }

    public required long Size { get; init; }

    public required FileTimestamps Times { get; init; }

    public required FileIdentity SourceIdentity { get; init; }

    public uint LinkCount { get; init; } = 1;

    public string? ShortName { get; init; }

    /// <summary>Point d'analyse brut (liens).</summary>
    public byte[]? Reparse { get; init; }

    public byte[]? SecurityDescriptor { get; init; }

    public List<ManifestStream> Streams { get; init; } = [];

    /// <summary>Pour un lien physique secondaire : chemin relatif du premier nom du groupe.</summary>
    public string? HardLinkPrimary { get; init; }

    /// <summary>Empreinte du flux principal (fichiers), calculée sur la source.</summary>
    public ContentHash? Hash { get; set; }

    /// <summary>Identité de l'élément créé dans la destination.</summary>
    public FileIdentity? DestinationIdentity { get; set; }

    public bool IsDirectoryLike => Kind == FsEntryKind.Directory || (Kind == FsEntryKind.Link && (Attributes & FileAttributes.Directory) != 0);

    public FileAttributes CopiedAttributeValue => Attributes & CopiedAttributes;
}

/// <summary>
/// Manifeste d'une arborescence : inventaire complet et empreintes de la SOURCE, dans l'ordre préfixe
/// (chaque dossier avant son contenu). Sert à copier, vérifier, puis supprimer exactement ce qui a été vérifié.
/// </summary>
public sealed class Manifest
{
    public required string SourceRoot { get; init; }

    public required List<ManifestEntry> Entries { get; init; }

    public ManifestEntry Root => Entries[0];

    public int FileCount => Entries.Count(e => e.Kind == FsEntryKind.File);

    public int DirectoryCount => Entries.Count(e => e.Kind == FsEntryKind.Directory);

    public int LinkCount => Entries.Count(e => e.Kind == FsEntryKind.Link);

    /// <summary>Octets à copier (un lien physique secondaire ne coûte rien de plus).</summary>
    public long TotalBytes => Entries.Where(e => e.Kind == FsEntryKind.File && e.HardLinkPrimary is null).Sum(e => e.Size + e.Streams.Sum(s => s.Size));

    public bool IsFullyHashed => Entries.Where(e => e.Kind == FsEntryKind.File).All(e => e.Hash is not null && e.Streams.All(s => s.Hash is not null));

    /// <summary>Construit le manifeste à partir d'un inventaire sans problème bloquant.</summary>
    public static Manifest FromSnapshot(TreeSnapshot snapshot)
    {
        if (snapshot.HasBlockingProblems)
        {
            throw new InvalidOperationException("Impossible de construire un manifeste : l'inventaire contient des problèmes bloquants.");
        }

        var primaries = new Dictionary<FileIdentity, string>();
        var entries = new List<ManifestEntry>(snapshot.Entries.Count);
        foreach (var e in snapshot.Entries)
        {
            string? primary = null;
            if (e.Kind == FsEntryKind.File && e.Info.LinkCount > 1)
            {
                if (primaries.TryGetValue(e.Info.Identity, out var first))
                {
                    primary = first;
                }
                else
                {
                    primaries[e.Info.Identity] = e.RelativePath;
                }
            }

            entries.Add(new ManifestEntry
            {
                RelativePath = e.RelativePath,
                Kind = e.Kind,
                Attributes = e.Info.Attributes,
                Size = e.Info.Size,
                Times = e.Info.Times,
                SourceIdentity = e.Info.Identity,
                LinkCount = e.Info.LinkCount,
                ShortName = e.ShortName,
                Reparse = e.Reparse?.Raw,
                SecurityDescriptor = e.SecurityDescriptor,
                Streams = e.Streams.Select(s => new ManifestStream { Name = s.Name, Size = s.Size }).ToList(),
                HardLinkPrimary = primary,
            });
        }

        return new Manifest { SourceRoot = snapshot.RootPath, Entries = entries };
    }
}

/// <summary>Avancement d'un transfert ou d'une vérification.</summary>
public sealed record TransferProgress(string Phase, long BytesDone, long BytesTotal, int ItemsDone, int ItemsTotal, string? CurrentItem);
