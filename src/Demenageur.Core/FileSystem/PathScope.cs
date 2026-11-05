namespace Demenageur.Core.FileSystem;

/// <summary>
/// Périmètre autorisé pour une opération. Tous les chemins sont canoniques (voir <see cref="PathGuard.Canonicalize"/>).
/// </summary>
public sealed class PathScope
{
    internal PathScope(
        IReadOnlyList<string> writableRoots,
        IReadOnlyList<string> protectedExact,
        IReadOnlyList<string> protectedSubtrees,
        string? sandboxRoot)
    {
        WritableRoots = writableRoots;
        ProtectedExact = protectedExact;
        ProtectedSubtrees = protectedSubtrees;
        SandboxRoot = sandboxRoot;
    }

    /// <summary>Racines (et leurs sous-arborescences) où les modifications sont autorisées.</summary>
    public IReadOnlyList<string> WritableRoots { get; }

    /// <summary>Chemins qui ne doivent jamais être modifiés eux-mêmes (leurs enfants peuvent l'être) : racines de lecteur, Program Files…</summary>
    public IReadOnlyList<string> ProtectedExact { get; }

    /// <summary>Arborescences entièrement intouchables : Windows, WindowsApps…</summary>
    public IReadOnlyList<string> ProtectedSubtrees { get; }

    /// <summary>Si défini (tests), TOUTE opération, lecture comprise, doit rester dans ce dossier.</summary>
    public string? SandboxRoot { get; }

    /// <summary>
    /// Construit un périmètre en canonicalisant chaque chemin, puis vérifie sa cohérence :
    /// aucune racine modifiable ne peut être une racine de lecteur, ni contenir ou valoir un chemin protégé.
    /// </summary>
    public static PathScope Create(
        IFileSystem inner,
        IEnumerable<string> writableRoots,
        IEnumerable<string>? protectedExact = null,
        IEnumerable<string>? protectedSubtrees = null,
        string? sandboxRoot = null)
    {
        // Une racine est prise pour elle-même (une jonction reste la jonction, pas sa cible) ; ses parents sont résolus.
        string Canon(string p) => PathGuard.Canonicalize(inner, p, followFinalLink: false);

        var sandbox = sandboxRoot is null ? null : Canon(sandboxRoot);
        var writable = writableRoots.Select(Canon).Distinct(StringComparer.Ordinal).ToList();
        var exact = (protectedExact ?? []).Select(Canon).Distinct(StringComparer.Ordinal).ToList();
        var subtrees = (protectedSubtrees ?? []).Select(Canon).Distinct(StringComparer.Ordinal).ToList();

        foreach (var root in writable)
        {
            if (PathUtil.IsDriveRoot(root))
            {
                throw new PathGuardViolationException($"Une racine de lecteur ne peut pas être modifiable : « {root} ».", root);
            }

            foreach (var p in exact.Concat(subtrees))
            {
                if (PathUtil.IsWithin(p, root))
                {
                    throw new PathGuardViolationException(
                        $"La racine modifiable « {root} » contient le chemin protégé « {p} ».", root);
                }
            }

            foreach (var p in subtrees)
            {
                if (PathUtil.IsWithin(root, p))
                {
                    throw new PathGuardViolationException(
                        $"La racine modifiable « {root} » est dans l'arborescence protégée « {p} ».", root);
                }
            }

            if (sandbox is not null && !PathUtil.IsWithin(root, sandbox))
            {
                throw new PathGuardViolationException(
                    $"La racine modifiable « {root} » est hors du bac à sable « {sandbox} ».", root);
            }
        }

        return new PathScope(writable, exact, subtrees, sandbox);
    }
}
