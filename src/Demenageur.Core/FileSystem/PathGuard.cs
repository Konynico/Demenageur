namespace Demenageur.Core.FileSystem;

/// <summary>
/// Garde de périmètre : enveloppe un <see cref="IFileSystem"/> et refuse toute opération dont la cible réelle
/// (après résolution des jonctions des dossiers parents et des noms 8.3) sort du périmètre autorisé.
/// Dernière ligne de défense contre un bug : même une erreur de calcul de chemin ne peut pas toucher
/// autre chose que la source, la destination et les dossiers temporaires de l'opération en cours.
/// </summary>
public sealed class PathGuard : IFileSystem
{
    private readonly IFileSystem _inner;

    public PathGuard(IFileSystem inner, PathScope scope)
    {
        _inner = inner;
        Scope = scope;
    }

    public PathScope Scope { get; }

    /// <summary>
    /// Forme canonique d'un chemin : chemin réel de l'élément s'il existe (sans suivre son propre point d'analyse
    /// si <paramref name="followFinalLink"/> est faux), sinon chemin réel du parent + nom.
    /// </summary>
    public static string Canonicalize(IFileSystem inner, string path, bool followFinalLink)
    {
        if (!PathUtil.TryValidateLocalPath(path, out var normalized, out var error))
        {
            throw new PathGuardViolationException($"Chemin refusé ({error}) : « {path} ».", path);
        }

        if (PathUtil.IsDriveRoot(normalized))
        {
            return normalized;
        }

        if (inner.GetEntry(normalized) is not null)
        {
            return PathUtil.TrimTrailingSeparator(inner.GetFinalPath(normalized, followFinalLink));
        }

        var parent = PathUtil.GetParent(normalized)
                     ?? throw new PathGuardViolationException($"Chemin sans parent : « {path} ».", path);
        return PathUtil.Join(Canonicalize(inner, parent, followFinalLink: true), PathUtil.GetName(normalized));
    }

    // ───────────── Vérifications ─────────────

    private string CheckRead(string path, bool followFinalLink = false)
    {
        var canonical = Canonicalize(_inner, path, followFinalLink);
        if (!Scope.IsInSandbox(canonical))
        {
            throw new PathGuardViolationException(
                $"Lecture hors du bac à sable refusée : « {path} » → « {canonical} ».", path);
        }

        return canonical;
    }

    private string CheckWrite(string path, bool followFinalLink = false)
    {
        var canonical = Canonicalize(_inner, path, followFinalLink);
        CheckWriteCanonical(canonical, path);
        return canonical;
    }

    private void CheckWriteCanonical(string canonical, string requested)
    {
        if (!Scope.IsInSandbox(canonical))
        {
            throw new PathGuardViolationException(
                $"Modification hors du bac à sable refusée : « {requested} » → « {canonical} ».", requested);
        }

        if (!Scope.WritableRoots.Any(root => PathUtil.IsWithin(canonical, root)))
        {
            throw new PathGuardViolationException(
                $"Modification hors du périmètre de l'opération refusée : « {requested} » → « {canonical} ».", requested);
        }

        foreach (var protectedRoot in Scope.ProtectedSubtrees)
        {
            if (PathUtil.IsWithin(canonical, protectedRoot))
            {
                throw new PathGuardViolationException(
                    $"Modification d'une arborescence protégée refusée : « {canonical} » (dans « {protectedRoot} »).", requested);
            }
        }

        foreach (var protectedPath in Scope.ProtectedExact)
        {
            if (string.Equals(PathUtil.TrimTrailingSeparator(canonical), protectedPath, StringComparison.Ordinal))
            {
                throw new PathGuardViolationException($"Modification d'un dossier protégé refusée : « {canonical} ».", requested);
            }
        }
    }

    // ───────────── Lecture ─────────────

    public FsEntryInfo? GetEntry(string path)
    {
        CheckRead(path);
        return _inner.GetEntry(path);
    }

    public FsEntryInfo? GetEntryFollowingLinks(string path)
    {
        CheckRead(path, followFinalLink: true);
        return _inner.GetEntryFollowingLinks(path);
    }

    public IReadOnlyList<DirectoryChild> ListDirectory(string directoryPath, FileIdentity expectedIdentity, bool followLink = false)
    {
        CheckRead(directoryPath, followLink);
        return _inner.ListDirectory(directoryPath, expectedIdentity, followLink);
    }

    public ReparsePointData ReadReparsePoint(string path, FileIdentity expectedIdentity)
    {
        CheckRead(path);
        return _inner.ReadReparsePoint(path, expectedIdentity);
    }

    public IReadOnlyList<AlternateStream> ListAlternateStreams(string filePath, FileIdentity expectedIdentity)
    {
        CheckRead(filePath);
        return _inner.ListAlternateStreams(filePath, expectedIdentity);
    }

    public ContentHash HashStream(string filePath, string? streamName, FileIdentity expectedIdentity, bool unbuffered, Action<long>? bytesRead, CancellationToken cancellationToken)
    {
        CheckRead(filePath);
        return _inner.HashStream(filePath, streamName, expectedIdentity, unbuffered, bytesRead, cancellationToken);
    }

    public byte[] GetSecurityDescriptor(string path, FileIdentity expectedIdentity)
    {
        CheckRead(path);
        return _inner.GetSecurityDescriptor(path, expectedIdentity);
    }

    public string GetFinalPath(string path, bool followFinalLink)
    {
        CheckRead(path, followFinalLink);
        return _inner.GetFinalPath(path, followFinalLink);
    }

    public VolumeInfo GetVolumeInfo(string path)
    {
        if (!PathUtil.TryValidateLocalPath(path, out _, out var error))
        {
            throw new PathGuardViolationException($"Chemin refusé ({error}) : « {path} ».", path);
        }

        return _inner.GetVolumeInfo(path);
    }

    public void ProbeRead(string filePath, FileIdentity expectedIdentity)
    {
        CheckRead(filePath);
        _inner.ProbeRead(filePath, expectedIdentity);
    }

    public byte[]? ReadAllBytes(string path)
    {
        CheckRead(path, followFinalLink: true);
        return _inner.ReadAllBytes(path);
    }

    // ───────────── Modifications ─────────────

    public FileIdentity CreateDirectory(string path, byte[]? securityDescriptor = null)
    {
        CheckWrite(path);
        return _inner.CreateDirectory(path, securityDescriptor);
    }

    public FileIdentity CopyFile(string sourcePath, FileIdentity expectedSourceIdentity, string destinationPath, CopyFileOptions options, Action<long>? bytesCopied, CancellationToken cancellationToken)
    {
        CheckRead(sourcePath);
        CheckWrite(destinationPath);
        return _inner.CopyFile(sourcePath, expectedSourceIdentity, destinationPath, options, bytesCopied, cancellationToken);
    }

    public FileIdentity CreateHardLink(string newLinkPath, string existingFilePath, FileIdentity expectedExistingIdentity)
    {
        CheckWrite(newLinkPath);
        CheckWrite(existingFilePath);
        return _inner.CreateHardLink(newLinkPath, existingFilePath, expectedExistingIdentity);
    }

    public FileIdentity CreateReparsePoint(string path, bool isDirectory, ReparsePointData data)
    {
        CheckWrite(path);
        return _inner.CreateReparsePoint(path, isDirectory, data);
    }

    public void Rename(string path, FileIdentity expectedIdentity, string newName)
    {
        if (newName.Length == 0 || newName.IndexOfAny(['\\', '/', ':']) >= 0 || newName is "." or "..")
        {
            throw new PathGuardViolationException($"Nouveau nom invalide : « {newName} ».", path);
        }

        var canonical = CheckWrite(path);
        var parent = PathUtil.GetParent(canonical)
                     ?? throw new PathGuardViolationException($"Impossible de renommer une racine : « {path} ».", path);
        CheckWriteCanonical(PathUtil.Join(parent, newName), PathUtil.Join(parent, newName));
        _inner.Rename(path, expectedIdentity, newName);
    }

    public void SetBasicInfo(string path, FileIdentity expectedIdentity, FileTimestamps times, FileAttributes attributes)
    {
        CheckWrite(path);
        _inner.SetBasicInfo(path, expectedIdentity, times, attributes);
    }

    public void SetSecurityDescriptor(string path, FileIdentity expectedIdentity, byte[] securityDescriptor, SecurityParts parts)
    {
        CheckWrite(path);
        _inner.SetSecurityDescriptor(path, expectedIdentity, securityDescriptor, parts);
    }

    public bool TrySetShortName(string path, FileIdentity expectedIdentity, string shortName)
    {
        CheckWrite(path);
        return _inner.TrySetShortName(path, expectedIdentity, shortName);
    }

    public void Delete(string path, FileIdentity expectedIdentity)
    {
        CheckWrite(path);
        _inner.Delete(path, expectedIdentity);
    }

    public void FlushFile(string path, FileIdentity expectedIdentity)
    {
        CheckWrite(path);
        _inner.FlushFile(path, expectedIdentity);
    }

    public bool TryFlushVolume(string path)
    {
        CheckRead(path);
        return _inner.TryFlushVolume(path);
    }

    public void AppendToFile(string path, ReadOnlySpan<byte> data)
    {
        CheckWrite(path);
        _inner.AppendToFile(path, data);
    }

    public void WriteNewFile(string path, ReadOnlySpan<byte> data)
    {
        CheckWrite(path);
        _inner.WriteNewFile(path, data);
    }
}
