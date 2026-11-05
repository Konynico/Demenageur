using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Copy;

public sealed record VerificationIssue(string RelativePath, string Message, bool IsWarning = false);

public sealed class VerificationResult
{
    public List<VerificationIssue> Issues { get; } = [];

    public int ItemsChecked { get; set; }

    public long BytesHashed { get; set; }

    public bool Success => Issues.All(i => i.IsWarning);

    public IEnumerable<VerificationIssue> Errors => Issues.Where(i => !i.IsWarning);

    public string Summary => Success
        ? $"Vérification réussie : {ItemsChecked} éléments, {BytesHashed:N0} octets relus."
        : $"Vérification ÉCHOUÉE : {Errors.Count()} écart(s). Premier : {Errors.First().RelativePath} — {Errors.First().Message}";
}

public sealed record VerificationOptions
{
    /// <summary>Relire et hacher tout le contenu (sinon : structure, tailles, dates et attributs seulement).</summary>
    public bool CompareContent { get; init; } = true;

    /// <summary>Lecture sans cache système (relecture réelle du disque).</summary>
    public bool Unbuffered { get; init; } = true;

    /// <summary>Comparer attributs, dates, noms courts.</summary>
    public bool CompareMetadata { get; init; } = true;

    /// <summary>Comparer les descripteurs de sécurité.</summary>
    public bool CompareSecurity { get; init; }

    /// <summary>La racine vérifiée est une jonction à suivre (vérification « à travers la jonction »).</summary>
    public bool FollowRootLink { get; init; }

    /// <summary>Exiger que chaque élément ait l'identité enregistrée lors de la copie.</summary>
    public bool CheckDestinationIdentities { get; init; } = true;
}

/// <summary>
/// Vérification indépendante d'une copie : nouveau parcours complet de la destination (sans suivre les liens),
/// comparé au manifeste de la source. Rien n'est présumé : éléments manquants ET en trop, nature, tailles,
/// contenu de chaque flux (relu sans cache), attributs, dates, liens, liens physiques, permissions.
/// </summary>
public sealed class TreeVerifier
{
    private readonly IFileSystem _fs;

    public TreeVerifier(IFileSystem fs)
    {
        _fs = fs;
    }

    public VerificationResult Verify(Manifest manifest, string destinationRoot, VerificationOptions? options = null, Action<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new VerificationOptions();
        var result = new VerificationResult();
        var snapshot = new TreeScanner(_fs).Scan(destinationRoot, new ScanOptions
        {
            IncludeStreams = true,
            IncludeSecurity = options.CompareSecurity,
            FollowRootLink = options.FollowRootLink,
        }, cancellationToken: cancellationToken);

        foreach (var problem in snapshot.Problems.Where(p => p.Severity == ScanProblemSeverity.Blocking))
        {
            result.Issues.Add(new VerificationIssue(problem.RelativePath, problem.Message));
        }

        var actual = snapshot.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var expectedPaths = new HashSet<string>(manifest.Entries.Select(e => e.RelativePath), StringComparer.Ordinal);
        foreach (var extra in actual.Keys.Where(k => !expectedPaths.Contains(k)))
        {
            result.Issues.Add(new VerificationIssue(extra, "Élément en trop dans la copie."));
        }

        var totalBytes = options.CompareContent ? manifest.TotalBytes : 0;
        var hashedIdentities = new Dictionary<FileIdentity, ManifestEntry>();
        var destinationGroups = new Dictionary<FileIdentity, FileIdentity>();

        foreach (var expected in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ItemsChecked++;
            progress?.Invoke(new TransferProgress("Vérification", result.BytesHashed, totalBytes, result.ItemsChecked, manifest.Entries.Count, expected.RelativePath));
            if (!actual.TryGetValue(expected.RelativePath, out var found))
            {
                result.Issues.Add(new VerificationIssue(expected.RelativePath, "Élément manquant dans la copie."));
                continue;
            }

            CompareEntry(expected, found, options, result);

            // Liens physiques : un groupe source ↔ un seul fichier destination, et réciproquement.
            if (expected.Kind == FsEntryKind.File)
            {
                if (destinationGroups.TryGetValue(found.Info.Identity, out var sourceGroup) && sourceGroup != expected.SourceIdentity)
                {
                    result.Issues.Add(new VerificationIssue(expected.RelativePath, "Deux fichiers distincts de la source partagent les mêmes données dans la copie."));
                }

                destinationGroups[found.Info.Identity] = expected.SourceIdentity;
            }

            if (options.CompareContent && expected.Kind == FsEntryKind.File)
            {
                if (hashedIdentities.TryGetValue(found.Info.Identity, out var alreadyHashed))
                {
                    if (alreadyHashed.SourceIdentity != expected.SourceIdentity)
                    {
                        result.Issues.Add(new VerificationIssue(expected.RelativePath, "Lien physique inattendu dans la copie."));
                    }

                    continue;
                }

                hashedIdentities[found.Info.Identity] = expected;
                CompareContent(expected, found, destinationRoot, options, result, cancellationToken);
            }
        }

        // Chaque groupe de liens physiques de la source doit correspondre à un seul fichier dans la copie.
        foreach (var group in manifest.Entries.Where(e => e.Kind == FsEntryKind.File && e.LinkCount > 1).GroupBy(e => e.SourceIdentity))
        {
            var identities = group.Select(e => actual.TryGetValue(e.RelativePath, out var a) ? a.Info.Identity : default).Distinct().Count();
            if (identities > 1)
            {
                result.Issues.Add(new VerificationIssue(group.First().RelativePath, "Les liens physiques n'ont pas été recréés : les noms ne partagent plus les mêmes données."));
            }
        }

        return result;
    }

    private static void CompareEntry(ManifestEntry expected, ScannedEntry found, VerificationOptions options, VerificationResult result)
    {
        var path = expected.RelativePath;
        if (expected.Kind != found.Kind)
        {
            result.Issues.Add(new VerificationIssue(path, $"Nature différente : {found.Kind} au lieu de {expected.Kind}."));
            return;
        }

        if (options.CheckDestinationIdentities && expected.DestinationIdentity is { } id && id != found.Info.Identity)
        {
            result.Issues.Add(new VerificationIssue(path, "Identité différente de l'élément créé lors de la copie (substitution ?)."));
        }

        if (expected.Kind == FsEntryKind.File)
        {
            if (expected.Size != found.Info.Size)
            {
                result.Issues.Add(new VerificationIssue(path, $"Taille différente : {found.Info.Size:N0} octets au lieu de {expected.Size:N0}."));
            }

            var expectedStreams = expected.Streams.ToDictionary(s => s.Name, s => s.Size, StringComparer.Ordinal);
            var actualStreams = found.Streams.ToDictionary(s => s.Name, s => s.Size, StringComparer.Ordinal);
            foreach (var (name, size) in expectedStreams)
            {
                if (!actualStreams.TryGetValue(name, out var actualSize))
                {
                    result.Issues.Add(new VerificationIssue(path, $"Flux « {name} » manquant."));
                }
                else if (actualSize != size)
                {
                    result.Issues.Add(new VerificationIssue(path, $"Flux « {name} » de taille différente."));
                }
            }

            foreach (var name in actualStreams.Keys.Where(n => !expectedStreams.ContainsKey(n)))
            {
                result.Issues.Add(new VerificationIssue(path, $"Flux « {name} » en trop."));
            }
        }

        if (expected.Kind == FsEntryKind.Link
            && (found.Reparse is null || !found.Reparse.Raw.AsSpan().SequenceEqual(expected.Reparse)))
        {
            result.Issues.Add(new VerificationIssue(path, "Le lien ne pointe plus vers la même cible."));
        }

        var isFollowedRoot = path.Length == 0 && options.FollowRootLink;
        if (options.CompareMetadata && !isFollowedRoot)
        {
            if ((expected.Attributes & ManifestEntry.CopiedAttributes) != (found.Info.Attributes & ManifestEntry.CopiedAttributes))
            {
                result.Issues.Add(new VerificationIssue(path, $"Attributs différents : {found.Info.Attributes & ManifestEntry.CopiedAttributes} au lieu de {expected.Attributes & ManifestEntry.CopiedAttributes}."));
            }

            if (expected.Times.LastWriteTime != found.Info.Times.LastWriteTime || expected.Times.CreationTime != found.Info.Times.CreationTime)
            {
                result.Issues.Add(new VerificationIssue(path, "Dates de création ou de modification différentes."));
            }

            if (expected.ShortName is not null && !string.Equals(expected.ShortName, found.ShortName, StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(new VerificationIssue(path, $"Nom court « {found.ShortName ?? "(aucun)"} » au lieu de « {expected.ShortName} ».", IsWarning: true));
            }
        }

        if (options.CompareSecurity && !isFollowedRoot && expected.SecurityDescriptor is { } expectedSd && found.SecurityDescriptor is { } actualSd)
        {
            var same = path.Length == 0
                ? SecurityDescriptorTools.EffectiveDaclEquals(expectedSd, actualSd) && SecurityDescriptorTools.IsDaclProtected(actualSd)
                : SecurityDescriptorTools.DaclEquals(expectedSd, actualSd);
            if (!same)
            {
                result.Issues.Add(new VerificationIssue(path,
                    "Permissions différentes de celles de l'original "
                    + $"(attendu {SecurityDescriptorTools.ToSddl(expectedSd, System.Security.AccessControl.AccessControlSections.Access)}, "
                    + $"trouvé {SecurityDescriptorTools.ToSddl(actualSd, System.Security.AccessControl.AccessControlSections.Access)})."));
            }

            if (!Equals(SecurityDescriptorTools.GetOwner(expectedSd), SecurityDescriptorTools.GetOwner(actualSd)))
            {
                result.Issues.Add(new VerificationIssue(path, "Propriétaire différent de celui de l'original.", IsWarning: true));
            }
        }
    }

    private void CompareContent(ManifestEntry expected, ScannedEntry found, string destinationRoot, VerificationOptions options, VerificationResult result, CancellationToken cancellationToken)
    {
        var path = PathUtil.JoinRelative(destinationRoot, expected.RelativePath);
        if (expected.Hash is null)
        {
            result.Issues.Add(new VerificationIssue(expected.RelativePath, "Empreinte de la source inconnue : vérification du contenu impossible."));
            return;
        }

        try
        {
            var hash = _fs.HashStream(path, null, found.Info.Identity, options.Unbuffered, n => result.BytesHashed += n, cancellationToken);
            if (hash != expected.Hash)
            {
                result.Issues.Add(new VerificationIssue(expected.RelativePath, "Contenu différent de l'original (empreinte XxHash128)."));
            }

            foreach (var stream in expected.Streams)
            {
                if (found.Streams.All(s => s.Name != stream.Name))
                {
                    continue; // déjà signalé
                }

                var streamHash = _fs.HashStream(path, stream.Name, found.Info.Identity, options.Unbuffered, n => result.BytesHashed += n, cancellationToken);
                if (stream.Hash is null || streamHash != stream.Hash)
                {
                    result.Issues.Add(new VerificationIssue(expected.RelativePath, $"Contenu du flux « {stream.Name} » différent de l'original."));
                }
            }
        }
        catch (FileSystemException ex) when (ex.Kind is not FsErrorKind.PathRejected)
        {
            result.Issues.Add(new VerificationIssue(expected.RelativePath, $"Relecture impossible : {ex.Message}"));
        }
    }
}
