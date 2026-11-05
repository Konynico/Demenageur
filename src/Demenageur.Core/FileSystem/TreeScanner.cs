namespace Demenageur.Core.FileSystem;

/// <summary>Options de parcours.</summary>
public sealed record ScanOptions
{
    /// <summary>Lire les descripteurs de sécurité.</summary>
    public bool IncludeSecurity { get; init; }

    /// <summary>Lire les flux de données nommés (ADS).</summary>
    public bool IncludeStreams { get; init; } = true;

    /// <summary>Si la racine est une jonction, la suivre (vérification « à travers la jonction »).</summary>
    public bool FollowRootLink { get; init; }

    public static ScanOptions Default { get; } = new();

    public static ScanOptions Full { get; } = new() { IncludeSecurity = true };
}

/// <summary>
/// Parcours d'une arborescence qui :
/// <list type="bullet">
/// <item>n'ignore AUCUN élément (cachés, système, inaccessibles → erreur, jamais de saut silencieux) ;</item>
/// <item>ne suit JAMAIS une jonction ou un lien rencontré dans l'arborescence ;</item>
/// <item>vérifie que chaque élément ouvert a bien l'identité annoncée par l'énumération de son dossier
/// (détecte une substitution en cours de parcours) ;</item>
/// <item>signale tout ce qui empêche un déplacement sûr (points d'analyse exotiques, fichiers chiffrés,
/// dossiers sensibles à la casse, liens physiques vers l'extérieur…).</item>
/// </list>
/// </summary>
public sealed class TreeScanner
{
    private readonly IFileSystem _fs;

    public TreeScanner(IFileSystem fs)
    {
        _fs = fs;
    }

    public TreeSnapshot Scan(string rootPath, ScanOptions? options = null, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= ScanOptions.Default;
        rootPath = PathUtil.TrimTrailingSeparator(rootPath);
        var entries = new List<ScannedEntry>();
        var problems = new List<ScanProblem>();

        var rootInfo = options.FollowRootLink ? _fs.GetEntryFollowingLinks(rootPath) : _fs.GetEntry(rootPath);
        if (rootInfo is null)
        {
            problems.Add(new ScanProblem(string.Empty, ScanProblemCode.RootMissing, ScanProblemSeverity.Blocking, $"Le dossier « {rootPath} » est introuvable."));
            return new TreeSnapshot(rootPath, entries, problems);
        }

        if (rootInfo.Kind == FsEntryKind.Link)
        {
            problems.Add(new ScanProblem(string.Empty, ScanProblemCode.RootIsLink, ScanProblemSeverity.Blocking, $"« {rootPath} » est déjà une jonction ou un lien."));
            return new TreeSnapshot(rootPath, entries, problems);
        }

        if (rootInfo.Kind != FsEntryKind.Directory)
        {
            problems.Add(new ScanProblem(string.Empty, ScanProblemCode.RootNotDirectory, ScanProblemSeverity.Blocking, $"« {rootPath} » n'est pas un dossier ordinaire."));
            return new TreeSnapshot(rootPath, entries, problems);
        }

        var stack = new Stack<(string RelativePath, FsEntryInfo Info, string? ShortName)>();
        stack.Push((string.Empty, rootInfo, null));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (relative, info, shortName) = stack.Pop();
            var fullPath = PathUtil.JoinRelative(rootPath, relative);
            var isRoot = relative.Length == 0;
            var entry = Describe(fullPath, relative, info, shortName, options, problems, isRoot);
            entries.Add(entry);
            progress?.Invoke(entries.Count);

            if (info.Kind != FsEntryKind.Directory)
            {
                continue;
            }

            IReadOnlyList<DirectoryChild> children;
            try
            {
                children = _fs.ListDirectory(fullPath, info.Identity, followLink: isRoot && options.FollowRootLink);
            }
            catch (FileSystemException ex) when (ex.Kind is FsErrorKind.AccessDenied or FsErrorKind.SharingViolation or FsErrorKind.IdentityMismatch or FsErrorKind.NotFound)
            {
                problems.Add(new ScanProblem(relative, ex.Kind == FsErrorKind.IdentityMismatch ? ScanProblemCode.IdentityChanged : ScanProblemCode.Inaccessible,
                    ScanProblemSeverity.Blocking, $"Dossier illisible « {fullPath} » : {ex.Message}"));
                continue;
            }

            // Ordre déterministe (ordinal, sans normalisation Unicode), empilé à l'envers pour un parcours préfixe.
            var sorted = children.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            for (var i = sorted.Count - 1; i >= 0; i--)
            {
                var child = sorted[i];
                var childRelative = isRoot ? child.Name : relative + "\\" + child.Name;
                var childPath = PathUtil.Join(fullPath, child.Name);
                FsEntryInfo? childInfo;
                try
                {
                    childInfo = _fs.GetEntry(childPath);
                }
                catch (FileSystemException ex) when (ex.Kind is FsErrorKind.AccessDenied or FsErrorKind.SharingViolation)
                {
                    problems.Add(new ScanProblem(childRelative, ScanProblemCode.Inaccessible, ScanProblemSeverity.Blocking, $"Élément illisible « {childPath} » : {ex.Message}"));
                    continue;
                }

                if (childInfo is null)
                {
                    problems.Add(new ScanProblem(childRelative, ScanProblemCode.Vanished, ScanProblemSeverity.Blocking, $"« {childPath} » a disparu pendant l'analyse."));
                    continue;
                }

                if (childInfo.Identity.FileIdLow64 != child.FileId64 || childInfo.Identity.VolumeSerial != info.Identity.VolumeSerial)
                {
                    problems.Add(new ScanProblem(childRelative, ScanProblemCode.IdentityChanged, ScanProblemSeverity.Blocking,
                        $"« {childPath} » a été remplacé pendant l'analyse (identité différente)."));
                    continue;
                }

                var childShort = child.ShortName is { Length: > 0 } s && !string.Equals(s, child.Name, StringComparison.Ordinal) ? s : null;
                stack.Push((childRelative, childInfo, childShort));
            }
        }

        CheckHardLinks(entries, problems);
        return new TreeSnapshot(rootPath, entries, problems);
    }

    private ScannedEntry Describe(string fullPath, string relative, FsEntryInfo info, string? shortName, ScanOptions options, List<ScanProblem> problems, bool isRoot)
    {
        ReparsePointData? reparse = null;
        IReadOnlyList<AlternateStream> streams = [];
        byte[]? security = null;

        switch (info.Kind)
        {
            case FsEntryKind.Link:
                reparse = _fs.ReadReparsePoint(fullPath, info.Identity);
                CheckLink(relative, fullPath, reparse, problems);
                break;

            case FsEntryKind.UnsupportedReparsePoint:
                problems.Add(new ScanProblem(relative, ScanProblemCode.UnsupportedReparsePoint, ScanProblemSeverity.Blocking,
                    $"« {fullPath} » est un {ReparseTags.Describe(info.ReparseTag)} : non pris en charge."));
                break;

            case FsEntryKind.Directory when info.IsCaseSensitiveDirectory:
                problems.Add(new ScanProblem(relative, ScanProblemCode.CaseSensitiveDirectory, ScanProblemSeverity.Blocking,
                    $"« {fullPath} » est un dossier sensible à la casse (WSL) : non pris en charge."));
                break;

            case FsEntryKind.File:
                if ((info.Attributes & FileAttributes.Encrypted) != 0)
                {
                    problems.Add(new ScanProblem(relative, ScanProblemCode.EncryptedFile, ScanProblemSeverity.Blocking,
                        $"« {fullPath} » est chiffré (EFS) : non pris en charge."));
                }

                if ((info.Attributes & FileAttributes.SparseFile) != 0)
                {
                    problems.Add(new ScanProblem(relative, ScanProblemCode.SparseFile, ScanProblemSeverity.Warning,
                        $"« {fullPath} » est un fichier creux : sa copie occupera toute sa taille logique."));
                }

                break;
        }

        // Racine suivie à travers une jonction : ses flux et permissions sont ceux de la cible, lus ailleurs.
        var followedRoot = isRoot && options.FollowRootLink;
        if (options.IncludeStreams && !followedRoot && info.Kind is FsEntryKind.File or FsEntryKind.Directory)
        {
            streams = _fs.ListAlternateStreams(fullPath, info.Identity);
        }

        if (options.IncludeSecurity && !followedRoot)
        {
            security = _fs.GetSecurityDescriptor(fullPath, info.Identity);
        }

        return new ScannedEntry
        {
            RelativePath = relative,
            Info = info,
            ShortName = shortName,
            Reparse = reparse,
            Streams = streams,
            SecurityDescriptor = security,
        };
    }

    private static void CheckLink(string relative, string fullPath, ReparsePointData reparse, List<ScanProblem> problems)
    {
        if (reparse.IsVolumeMountPoint)
        {
            problems.Add(new ScanProblem(relative, ScanProblemCode.VolumeMountPoint, ScanProblemSeverity.Blocking,
                $"« {fullPath} » est un point de montage de volume : non pris en charge."));
            return;
        }

        if (reparse.Tag == ReparseTags.Symlink && reparse.IsRelative)
        {
            var parent = PathUtil.GetParent(fullPath) ?? fullPath;
            var resolved = reparse.Target is null ? null : PathUtil.ResolveRelative(parent, reparse.Target);
            var root = fullPath[..^relative.Length].TrimEnd('\\');
            if (resolved is null || !PathUtil.IsWithinIgnoreCase(resolved, root))
            {
                problems.Add(new ScanProblem(relative, ScanProblemCode.RelativeLinkEscapes, ScanProblemSeverity.Blocking,
                    $"Le lien relatif « {fullPath} » pointe hors du dossier ({reparse.Target}) : il ne fonctionnerait plus après le déplacement."));
                return;
            }
        }

        problems.Add(new ScanProblem(relative, ScanProblemCode.LinkInside, ScanProblemSeverity.Warning,
            $"« {fullPath} » est un {ReparseTags.Describe(reparse.Tag)} vers « {reparse.Target} » : il sera recréé à l'identique."));
    }

    private static void CheckHardLinks(List<ScannedEntry> entries, List<ScanProblem> problems)
    {
        foreach (var group in entries.Where(e => e.Kind == FsEntryKind.File && e.Info.LinkCount > 1).GroupBy(e => e.Info.Identity))
        {
            var names = group.ToList();
            var linkCount = names[0].Info.LinkCount;
            if (names.Count < linkCount)
            {
                foreach (var entry in names)
                {
                    problems.Add(new ScanProblem(entry.RelativePath, ScanProblemCode.HardLinkOutside, ScanProblemSeverity.Blocking,
                        $"« {entry.RelativePath} » partage ses données avec un fichier situé hors du dossier (lien physique, {linkCount} noms)."));
                }
            }
            else
            {
                problems.Add(new ScanProblem(names[0].RelativePath, ScanProblemCode.HardLinkInside, ScanProblemSeverity.Warning,
                    $"{names.Count} noms partagent les mêmes données (liens physiques) : ils seront recréés à l'identique."));
            }
        }
    }
}
