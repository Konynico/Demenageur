namespace Demenageur.Core.FileSystem;

/// <summary>
/// Manipulations de chemins purement textuelles, SANS normalisation Win32
/// (qui supprimerait les points et espaces finaux des noms).
/// </summary>
public static class PathUtil
{
    public const string ExtendedPrefix = @"\\?\";

    public static string Join(string parent, string name)
        => parent.EndsWith('\\') ? parent + name : parent + "\\" + name;

    /// <summary>Joint une racine et un chemin relatif de manifeste (« » = la racine elle-même).</summary>
    public static string JoinRelative(string root, string relativePath)
        => relativePath.Length == 0 ? root : Join(root, relativePath);

    public static string StripExtendedPrefix(string path)
        => path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[ExtendedPrefix.Length..]
            : path;

    /// <summary>Retire la barre oblique finale, sauf pour une racine de lecteur (<c>C:\</c>).</summary>
    public static string TrimTrailingSeparator(string path)
    {
        while (path.Length > 3 && path.EndsWith('\\'))
        {
            path = path[..^1];
        }

        return path;
    }

    public static bool IsDriveRoot(string path)
        => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    public static string GetName(string path)
    {
        path = TrimTrailingSeparator(path);
        var index = path.LastIndexOf('\\');
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>Dossier parent, ou null pour une racine de lecteur.</summary>
    public static string? GetParent(string path)
    {
        path = TrimTrailingSeparator(path);
        if (IsDriveRoot(path))
        {
            return null;
        }

        var index = path.LastIndexOf('\\');
        if (index < 0)
        {
            return null;
        }

        return index == 2 ? path[..3] : path[..index];
    }

    /// <summary>
    /// Vrai si <paramref name="path"/> est <paramref name="root"/> ou se trouve dessous.
    /// Comparaison ordinale (sensible à la casse) sur des formes canoniques : plus stricte, donc plus sûre.
    /// </summary>
    public static bool IsWithin(string path, string root)
    {
        path = TrimTrailingSeparator(path);
        root = TrimTrailingSeparator(root);
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Comme <see cref="IsWithin"/> mais insensible à la casse (pour des chemins saisis par l'utilisateur).</summary>
    public static bool IsWithinIgnoreCase(string path, string root)
    {
        path = TrimTrailingSeparator(path);
        root = TrimTrailingSeparator(root);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Vérifie qu'un chemin est un chemin local complet acceptable : <c>X:\…</c> ou <c>\\?\X:\…</c>,
    /// sans segment « . » ou « .. », sans syntaxe de flux (<c>:</c>), sans caractère interdit.
    /// Renvoie la forme sans préfixe <c>\\?\</c>, ou un message d'erreur.
    /// </summary>
    public static bool TryValidateLocalPath(string path, out string normalized, out string? error)
    {
        normalized = path;
        error = null;
        if (string.IsNullOrEmpty(path))
        {
            error = "chemin vide";
            return false;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            error = "chemin réseau ou de périphérique refusé";
            return false;
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            error = "seuls les chemins locaux complets (X:\\…) sont acceptés";
            return false;
        }

        foreach (var c in path.AsSpan(2))
        {
            if (c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '/')
            {
                error = $"caractère interdit « {(c < 32 ? "\\u" + ((int)c).ToString("X4") : c.ToString())} »";
                return false;
            }
        }

        var trimmed = TrimTrailingSeparator(path);
        if (!IsDriveRoot(trimmed))
        {
            foreach (var segment in trimmed[3..].Split('\\'))
            {
                if (segment.Length == 0)
                {
                    error = "segment vide";
                    return false;
                }

                if (segment is "." or "..")
                {
                    error = "segment « . » ou « .. » refusé";
                    return false;
                }
            }
        }

        normalized = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return true;
    }

    /// <summary>Résout textuellement une cible relative (lien symbolique) depuis un dossier, en gérant « . » et « .. ».</summary>
    public static string? ResolveRelative(string baseDirectory, string relativeTarget)
    {
        var parts = new List<string>(TrimTrailingSeparator(baseDirectory).Split('\\', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in relativeTarget.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count <= 1)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        var result = string.Join('\\', parts);
        return result.Length == 2 ? result + "\\" : result;
    }
}
