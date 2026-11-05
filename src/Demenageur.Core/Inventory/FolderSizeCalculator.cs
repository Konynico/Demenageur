using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Inventory;

/// <summary>Taille d'un dossier.</summary>
public sealed record FolderSize(long Bytes, int Files, int Directories, bool Partial);

/// <summary>
/// Calcul rapide de la taille réelle d'un dossier (pour la liste des logiciels) : seuls les dossiers sont ouverts,
/// les tailles viennent de l'énumération. Les jonctions et liens ne sont jamais suivis ; un dossier illisible rend
/// le résultat « partiel ». (La simulation d'un déplacement refait, elle, un inventaire complet et vérifié.)
/// </summary>
public sealed class FolderSizeCalculator
{
    private readonly IFileSystem _fs;

    public FolderSizeCalculator(IFileSystem fs)
    {
        _fs = fs;
    }

    public FolderSize Compute(string folder, CancellationToken cancellationToken = default)
    {
        var root = _fs.GetEntryFollowingLinks(folder);
        if (root is null || root.Kind != FsEntryKind.Directory)
        {
            return new FolderSize(0, 0, 0, true);
        }

        long bytes = 0;
        var files = 0;
        var directories = 1;
        var partial = false;
        var isRoot = true;
        var stack = new Stack<(string Path, FileIdentity Identity)>();
        stack.Push((folder, root.Identity));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, identity) = stack.Pop();
            IReadOnlyList<DirectoryChild> children;
            try
            {
                children = _fs.ListDirectory(path, identity, followLink: isRoot);
            }
            catch (FileSystemException)
            {
                partial = true;
                continue;
            }
            finally
            {
                isRoot = false;
            }

            foreach (var child in children)
            {
                var isReparse = (child.Attributes & FileAttributes.ReparsePoint) != 0;
                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!isReparse)
                    {
                        directories++;
                        stack.Push((PathUtil.Join(path, child.Name), new FileIdentity(identity.VolumeSerial, child.FileId64)));
                    }
                }
                else if (!isReparse || child.Size > 0)
                {
                    files++;
                    bytes += child.Size;
                }
            }
        }

        return new FolderSize(bytes, files, directories, partial);
    }
}
