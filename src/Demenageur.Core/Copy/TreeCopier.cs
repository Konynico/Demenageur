using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Copy;

/// <summary>Avertissement non bloquant relevé pendant la copie (nom court non recopié, propriétaire non conservé…).</summary>
public sealed record CopyWarning(string RelativePath, string Message);

/// <summary>
/// Copie fidèle d'une arborescence décrite par un <see cref="Manifest"/> vers une destination vide déjà créée.
/// <list type="number">
/// <item>Dossiers, fichiers (CopyFile2 : données, flux ADS, attributs étendus), liens recréés octet pour octet ;
/// empreinte de chaque fichier source calculée au passage.</item>
/// <item>Liens physiques recréés entre les copies.</item>
/// <item>Vidage sur disque, puis attributs et horodatages exacts (dossiers en dernier, du plus profond au plus haut).</item>
/// </list>
/// Les permissions définitives NE sont PAS posées ici : la destination garde un accès privé tant que
/// le contenu n'a pas été vérifié (voir <see cref="ApplySecurity"/>).
/// </summary>
public sealed class TreeCopier
{
    /// <summary>Au-delà, copie sans cache système.</summary>
    public const long NoBufferingThreshold = 8L * 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly RetryPolicy _retry;

    public TreeCopier(IFileSystem fs, RetryPolicy? retry = null)
    {
        _fs = fs;
        _retry = retry ?? RetryPolicy.Default;
    }

    public List<CopyWarning> Warnings { get; } = [];

    public void Copy(Manifest manifest, string destinationRoot, FileIdentity destinationRootIdentity, Action<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = _fs.GetEntry(destinationRoot) ?? throw new FileSystemException(FsErrorKind.NotFound, $"Destination « {destinationRoot} » introuvable.", destinationRoot);
        if (root.Identity != destinationRootIdentity || root.Kind != FsEntryKind.Directory)
        {
            throw new FileSystemException(FsErrorKind.IdentityMismatch, $"La destination « {destinationRoot} » n'est pas le dossier créé pour cette opération.", destinationRoot);
        }

        manifest.Root.DestinationIdentity = destinationRootIdentity;
        var byPath = manifest.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var totalBytes = manifest.TotalBytes;
        var totalItems = manifest.Entries.Count;
        long bytesDone = 0;
        var itemsDone = 1;

        void Report(string? item) => progress?.Invoke(new TransferProgress("Copie", bytesDone, totalBytes, itemsDone, totalItems, item));

        // 1) Création des éléments, dans l'ordre préfixe.
        var secondaries = new List<ManifestEntry>();
        foreach (var entry in manifest.Entries.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = PathUtil.JoinRelative(manifest.SourceRoot, entry.RelativePath);
            var destination = PathUtil.JoinRelative(destinationRoot, entry.RelativePath);
            Report(entry.RelativePath);

            switch (entry.Kind)
            {
                case FsEntryKind.Directory:
                    entry.DestinationIdentity = _retry.Execute(() => _fs.CreateDirectory(destination), cancellationToken);
                    break;

                case FsEntryKind.Link:
                    var reparse = ReparsePointData.Parse(entry.Reparse ?? throw new InvalidOperationException($"Lien sans données : {entry.RelativePath}"));
                    entry.DestinationIdentity = _retry.Execute(() => _fs.CreateReparsePoint(destination, entry.IsDirectoryLike, reparse), cancellationToken);
                    break;

                case FsEntryKind.File when entry.HardLinkPrimary is not null:
                    secondaries.Add(entry);
                    continue;

                case FsEntryKind.File:
                    var options = entry.Size >= NoBufferingThreshold ? CopyFileOptions.NoBuffering : CopyFileOptions.None;
                    var before = bytesDone;
                    entry.DestinationIdentity = _retry.Execute(
                        () =>
                        {
                            bytesDone = before;
                            return _fs.CopyFile(source, entry.SourceIdentity, destination, options, n =>
                            {
                                bytesDone += n;
                                Report(entry.RelativePath);
                            }, cancellationToken);
                        },
                        cancellationToken);
                    bytesDone = before + entry.Size + entry.Streams.Sum(s => s.Size);

                    // CopyFile2 recopie la lecture seule : on la retire le temps du vidage disque (reposée à l'étape 4).
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        var withoutReadOnly = entry.CopiedAttributeValue & ~FileAttributes.ReadOnly;
                        _retry.Execute(() => _fs.SetBasicInfo(destination, entry.DestinationIdentity!.Value, default, withoutReadOnly == 0 ? FileAttributes.Normal : withoutReadOnly), cancellationToken);
                    }

                    // Empreintes de la SOURCE (identité vérifiée) : référence de la vérification.
                    entry.Hash = _retry.Execute(() => _fs.HashStream(source, null, entry.SourceIdentity, unbuffered: false, null, cancellationToken), cancellationToken);
                    foreach (var stream in entry.Streams)
                    {
                        stream.Hash = _retry.Execute(() => _fs.HashStream(source, stream.Name, entry.SourceIdentity, unbuffered: false, null, cancellationToken), cancellationToken);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Élément non pris en charge dans le manifeste : {entry.RelativePath} ({entry.Kind}).");
            }

            ApplyShortName(entry, destination);
            itemsDone++;
        }

        // 2) Liens physiques : nouveaux noms pour les fichiers déjà copiés.
        foreach (var entry in secondaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primary = byPath[entry.HardLinkPrimary!];
            var destination = PathUtil.JoinRelative(destinationRoot, entry.RelativePath);
            entry.DestinationIdentity = _retry.Execute(
                () => _fs.CreateHardLink(destination, PathUtil.JoinRelative(destinationRoot, primary.RelativePath), primary.DestinationIdentity!.Value),
                cancellationToken);
            entry.Hash = primary.Hash;
            for (var i = 0; i < entry.Streams.Count; i++)
            {
                entry.Streams[i].Hash = primary.Streams.ElementAtOrDefault(i)?.Hash;
            }

            ApplyShortName(entry, destination);
            itemsDone++;
        }

        // 3) Vidage sur disque AVANT de poser la lecture seule (qui interdirait l'ouverture en écriture).
        Report(null);
        if (!_fs.TryFlushVolume(destinationRoot))
        {
            foreach (var entry in manifest.Entries.Where(e => e.Kind == FsEntryKind.File && e.HardLinkPrimary is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PathUtil.JoinRelative(destinationRoot, entry.RelativePath);
                _retry.Execute(() => _fs.FlushFile(destination, entry.DestinationIdentity!.Value), cancellationToken);
            }
        }

        // 4) Attributs et horodatages : fichiers et liens, puis dossiers du plus profond au plus haut
        //    (créer un enfant modifie la date du dossier parent).
        foreach (var entry in manifest.Entries.Where(e => e.Kind != FsEntryKind.Directory && e.HardLinkPrimary is null))
        {
            ApplyBasicInfo(entry, destinationRoot, cancellationToken);
        }

        for (var i = manifest.Entries.Count - 1; i >= 0; i--)
        {
            if (manifest.Entries[i].Kind == FsEntryKind.Directory)
            {
                ApplyBasicInfo(manifest.Entries[i], destinationRoot, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Pose les permissions définitives, une fois le contenu vérifié : descripteurs recopiés tels quels pour
    /// chaque élément, puis, pour la racine, les droits effectifs de la source rendus explicites et protégés.
    /// Sans ce soin, la copie hériterait des droits du disque cible (souvent modifiables par tous).
    /// </summary>
    /// <param name="rootSecurity">
    /// Descripteur à poser tel quel sur la racine (annulation : permissions d'origine, héritage compris).
    /// Null : droits effectifs de la source rendus explicites et protégés.
    /// </param>
    public void ApplySecurity(Manifest manifest, string destinationRoot, byte[]? rootSecurity = null, CancellationToken cancellationToken = default)
    {
        foreach (var entry in manifest.Entries.Skip(1).Where(e => e.HardLinkPrimary is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.SecurityDescriptor is { } sd)
            {
                SetSecurity(entry, PathUtil.JoinRelative(destinationRoot, entry.RelativePath), sd, cancellationToken);
            }
        }

        var root = manifest.Root;
        if (rootSecurity is not null)
        {
            SetSecurity(root, destinationRoot, rootSecurity, cancellationToken);
        }
        else if (root.SecurityDescriptor is { } rootSd)
        {
            SetSecurity(root, destinationRoot, SecurityDescriptorTools.ToProtectedExplicit(rootSd), cancellationToken);
        }

        // NTFS pose l'attribut « Archive » quand le descripteur change : on repose les attributs exacts
        // (horodatages inchangés : valeur 0).
        foreach (var entry in manifest.Entries.Where(e => e.HardLinkPrimary is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = entry.CopiedAttributeValue;
            if (attributes == 0 && entry.Kind == FsEntryKind.Link)
            {
                continue;
            }

            var destination = PathUtil.JoinRelative(destinationRoot, entry.RelativePath);
            _retry.Execute(() => _fs.SetBasicInfo(destination, entry.DestinationIdentity!.Value, default, attributes == 0 ? FileAttributes.Normal : attributes), cancellationToken);
        }
    }

    private void SetSecurity(ManifestEntry entry, string destination, byte[] sd, CancellationToken cancellationToken)
    {
        var identity = entry.DestinationIdentity ?? throw new InvalidOperationException($"Élément non copié : {entry.RelativePath}");
        try
        {
            _retry.Execute(() => _fs.SetSecurityDescriptor(destination, identity, sd, SecurityParts.All), cancellationToken);
        }
        catch (FileSystemException ex) when (ex.Kind is not FsErrorKind.IdentityMismatch)
        {
            // Sans privilège de restauration, on ne peut pas attribuer un propriétaire quelconque : on garde les droits.
            _retry.Execute(() => _fs.SetSecurityDescriptor(destination, identity, sd, SecurityParts.Group | SecurityParts.Dacl), cancellationToken);
            Warnings.Add(new CopyWarning(entry.RelativePath, "Propriétaire d'origine non conservé (privilège de restauration absent)."));
        }
    }

    private void ApplyShortName(ManifestEntry entry, string destination)
    {
        if (entry.ShortName is null || entry.DestinationIdentity is null)
        {
            return;
        }

        if (!_fs.TrySetShortName(destination, entry.DestinationIdentity.Value, entry.ShortName))
        {
            Warnings.Add(new CopyWarning(entry.RelativePath, $"Nom court « {entry.ShortName} » non recopié."));
        }
    }

    private void ApplyBasicInfo(ManifestEntry entry, string destinationRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = PathUtil.JoinRelative(destinationRoot, entry.RelativePath);
        var attributes = entry.CopiedAttributeValue;
        if (attributes == 0 && entry.Kind != FsEntryKind.Link)
        {
            attributes = FileAttributes.Normal;
        }

        _retry.Execute(() => _fs.SetBasicInfo(destination, entry.DestinationIdentity!.Value, entry.Times, attributes), cancellationToken);
    }
}
