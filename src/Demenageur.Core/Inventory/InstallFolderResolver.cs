using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Inventory;

/// <summary>
/// Détermine le dossier d'installation d'une entrée de désinstallation :
/// <c>InstallLocation</c> s'il existe, sinon déduction depuis l'icône ou la commande de désinstallation
/// (en remontant les sous-dossiers typiques comme <c>bin</c>), résultat alors à confirmer par l'utilisateur.
/// Les emplacements génériques (Windows, caches d'installation…) ne sont jamais proposés.
/// </summary>
public sealed class InstallFolderResolver
{
    private static readonly HashSet<string> TypicalSubfolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "x64", "x86", "win32", "win64", "program", "app", "lib", "resources", "current", "uninstall", "uninst",
    };

    private readonly IFileSystem _fs;
    private readonly IKnownPaths _known;

    public InstallFolderResolver(IFileSystem fs, IKnownPaths known)
    {
        _fs = fs;
        _known = known;
    }

    public ResolvedFolder Resolve(UninstallEntry entry)
    {
        // InstallLocation est gardé même s'il désigne un emplacement système : la liste noire l'affichera « Bloqué ».
        if (CleanPath(entry.InstallLocation) is { } location && IsExistingDirectory(location) && !IsKnownRoot(location))
        {
            return new ResolvedFolder(location, FolderSource.InstallLocation);
        }

        if (ExtractExecutablePath(entry.DisplayIcon) is { } icon && Deduce(icon) is { } fromIcon)
        {
            return new ResolvedFolder(fromIcon, FolderSource.DisplayIcon);
        }

        if (entry.UninstallString is { } uninstall
            && !uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
            && ExtractExecutablePath(uninstall) is { } uninstaller
            && Deduce(uninstaller) is { } fromUninstaller)
        {
            return new ResolvedFolder(fromUninstaller, FolderSource.UninstallString);
        }

        return ResolvedFolder.NotFound;
    }

    private string? Deduce(string executable)
    {
        var folder = PathUtil.GetParent(executable);
        if (folder is null || !IsExistingDirectory(folder))
        {
            return null;
        }

        // Remonte « …\Produit\bin » vers « …\Produit », sans jamais dépasser une racine connue.
        while (TypicalSubfolders.Contains(PathUtil.GetName(folder))
               && PathUtil.GetParent(folder) is { } parent
               && !IsKnownRoot(parent))
        {
            folder = parent;
        }

        return IsGenericLocation(folder) || IsKnownRoot(folder) ? null : folder;
    }

    private bool IsExistingDirectory(string path)
    {
        try
        {
            return _fs.GetEntry(path) is { Kind: FsEntryKind.Directory or FsEntryKind.Link };
        }
        catch (FileSystemException)
        {
            return false;
        }
    }

    /// <summary>Racines sous lesquelles on trouve des dossiers de logiciels (jamais déplaçables elles-mêmes).</summary>
    public bool IsKnownRoot(string path)
    {
        path = PathUtil.TrimTrailingSeparator(path);
        IEnumerable<string> roots =
        [
            _known.ProgramFiles, _known.ProgramFilesX86, _known.CommonProgramFiles, _known.CommonProgramFilesX86,
            _known.ProgramData, _known.UsersDirectory, _known.SystemDrive + "\\",
            .. _known.UserProfiles,
            .. _known.UserProfiles.Select(p => PathUtil.Join(p, @"AppData\Local")),
            .. _known.UserProfiles.Select(p => PathUtil.Join(p, @"AppData\Local\Programs")),
            .. _known.UserProfiles.Select(p => PathUtil.Join(p, @"AppData\Roaming")),
        ];
        return PathUtil.IsDriveRoot(path) || roots.Any(r => string.Equals(PathUtil.TrimTrailingSeparator(r), path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Emplacements qui ne sont jamais « le dossier d'un logiciel » (caches d'installation, Windows…).</summary>
    public bool IsGenericLocation(string path)
        => PathUtil.IsWithinIgnoreCase(path, _known.WindowsDirectory)
           || PathUtil.IsWithinIgnoreCase(path, PathUtil.Join(_known.ProgramData, "Package Cache"))
           || PathUtil.IsWithinIgnoreCase(path, PathUtil.Join(_known.ProgramData, @"Microsoft\Windows"))
           || IsKnownRoot(path);

    /// <summary>Nettoie une valeur de chemin du registre (guillemets, espaces, barre finale).</summary>
    public static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim().Trim('"').Trim();
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\')
        {
            return null;
        }

        return PathUtil.TrimTrailingSeparator(path);
    }

    /// <summary>
    /// Extrait le chemin d'un exécutable d'une commande ou d'une valeur d'icône :
    /// <c>"C:\A\b.exe" /x</c>, <c>C:\A\b.exe,0</c>, <c>C:\A B\c.exe -arg</c>.
    /// </summary>
    public static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        string candidate;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            candidate = end > 1 ? text[1..end] : text.Trim('"');
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out _))
            {
                text = text[..comma];
            }

            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? text[..(exe + 4)] : text;
        }

        var comma2 = candidate.LastIndexOf(',');
        if (comma2 > 0 && int.TryParse(candidate[(comma2 + 1)..].Trim(), out _))
        {
            candidate = candidate[..comma2];
        }

        return CleanPath(candidate);
    }
}
