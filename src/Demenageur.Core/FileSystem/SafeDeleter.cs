namespace Demenageur.Core.FileSystem;

/// <summary>Résultat d'une suppression d'arborescence.</summary>
public sealed record DeletionResult(int Deleted, int AlreadyAbsent, IReadOnlyList<DeletionFailure> Failures)
{
    public bool Complete => Failures.Count == 0;
}

public sealed record DeletionFailure(string Path, FsErrorKind Kind, string Message);

/// <summary>
/// Suppression sûre : élément par élément, du plus profond au plus haut, chacun ouvert sans suivre les liens
/// et vérifié par son identité avant suppression. Ne supprime JAMAIS un élément absent de l'inventaire fourni :
/// un dossier qui contient quelque chose d'inconnu est laissé en place et signalé.
/// </summary>
public sealed class SafeDeleter
{
    private readonly IFileSystem _fs;
    private readonly RetryPolicy _retry;

    public SafeDeleter(IFileSystem fs, RetryPolicy? retry = null)
    {
        _fs = fs;
        _retry = retry ?? RetryPolicy.Default;
    }

    /// <summary>Supprime exactement les éléments de l'inventaire (racine comprise).</summary>
    public DeletionResult DeleteSnapshot(TreeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        var absent = 0;
        var failures = new List<DeletionFailure>();

        // Ordre inverse du parcours préfixe : chaque enfant avant son dossier.
        for (var i = snapshot.Entries.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = snapshot.Entries[i];
            var path = PathUtil.JoinRelative(snapshot.RootPath, entry.RelativePath);
            try
            {
                var current = _fs.GetEntry(path);
                if (current is null)
                {
                    absent++;
                    continue;
                }

                _retry.Execute(() => _fs.Delete(path, entry.Info.Identity), cancellationToken);
                deleted++;
            }
            catch (FileSystemException ex)
            {
                failures.Add(new DeletionFailure(path, ex.Kind, ex.Kind == FsErrorKind.DirectoryNotEmpty
                    ? $"« {path} » contient des éléments inconnus de l'inventaire : laissé en place."
                    : ex.Message));
            }
        }

        return new DeletionResult(deleted, absent, failures);
    }

    /// <summary>
    /// Supprime une arborescence créée par Déménageur (copie partielle), après avoir vérifié l'identité de sa racine.
    /// Le parcours ne suit aucun lien : une jonction est supprimée, jamais sa cible.
    /// </summary>
    public DeletionResult DeleteTree(string rootPath, FileIdentity expectedRootIdentity, CancellationToken cancellationToken = default)
    {
        var root = _fs.GetEntry(rootPath);
        if (root is null)
        {
            return new DeletionResult(0, 1, []);
        }

        if (root.Identity != expectedRootIdentity)
        {
            throw new FileSystemException(FsErrorKind.IdentityMismatch,
                $"« {rootPath} » n'est pas le dossier créé par Déménageur (identité différente) : suppression refusée.", rootPath);
        }

        // Ce qui n'a pas pu être inventorié n'est pas supprimé : le dossier parent restera, et sera signalé.
        var snapshot = new TreeScanner(_fs).Scan(rootPath, new ScanOptions { IncludeStreams = false }, cancellationToken: cancellationToken);
        return DeleteSnapshot(snapshot, cancellationToken);
    }
}
