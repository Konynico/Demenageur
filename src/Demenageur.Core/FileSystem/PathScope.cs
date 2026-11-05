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
        IReadOnlyList<string> sandboxRoots)
    {
        WritableRoots = writableRoots;
        ProtectedExact = protectedExact;
        ProtectedSubtrees = protectedSubtrees;
        SandboxRoots = sandboxRoots;
    }

    /// <summary>Racines (et leurs sous-arborescences) où les modifications sont autorisées.</summary>
    public IReadOnlyList<string> WritableRoots { get; }

    /// <summary>Chemins qui ne doivent jamais être modifiés eux-mêmes (leurs enfants peuvent l'être) : racines de lecteur, Program Files…</summary>
    public IReadOnlyList<string> ProtectedExact { get; }

    /// <summary>Arborescences entièrement intouchables : Windows, WindowsApps…</summary>
    public IReadOnlyList<string> ProtectedSubtrees { get; }

    /// <summary>Si non vide (tests), TOUTE opération, lecture comprise, doit rester dans l'un de ces dossiers.</summary>
    public IReadOnlyList<string> SandboxRoots { get; }

    public bool IsInSandbox(string canonicalPath)
        => SandboxRoots.Count == 0 || SandboxRoots.Any(root => PathUtil.IsWithin(canonicalPath, root));

    /// <summary>
    /// Construit un périmètre en canonicalisant chaque chemin, puis vérifie sa cohérence :
    /// aucune racine modifiable ne peut être une racine de lecteur, ni contenir ou valoir un chemin protégé.
    /// </summary>
    public static PathScope Create(
        IFileSystem inner,
        IEnumerable<string> writableRoots,
        IEnumerable<string>? protectedExact = null,
        IEnumerable<string>? protectedSubtrees = null,
        string? sandboxRoot = null,
        IEnumerable<string>? additionalSandboxRoots = null)
    {
        // Une racine est prise pour elle-même (une jonction reste la jonction, pas sa cible) ; ses parents sont résolus.
        string Canon(string p) => PathGuard.Canonicalize(inner, p, followFinalLink: false);

        var sandboxes = (sandboxRoot is null ? [] : new[] { sandboxRoot }).Concat(additionalSandboxRoots ?? []).Select(Canon).Distinct(StringComparer.Ordinal).ToList();
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

            if (sandboxes.Count > 0 && !sandboxes.Any(s => PathUtil.IsWithin(root, s)))
            {
                throw new PathGuardViolationException(
                    $"La racine modifiable « {root} » est hors du bac à sable « {string.Join(" | ", sandboxes)} ».", root);
            }
        }

        return new PathScope(writable, exact, subtrees, sandboxes);
    }
}
