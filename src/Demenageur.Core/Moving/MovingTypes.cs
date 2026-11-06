using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Moving;

/// <summary>Demande de déplacement d'un dossier S vers T (T ne doit pas exister ; son parent doit exister).</summary>
public sealed record MoveRequest(string SoftwareName, string SourcePath, string DestinationPath);

/// <summary>Demande d'annulation d'un déplacement terminé.</summary>
public sealed record UndoRequest
{
    public required string MoveOperationId { get; init; }

    public required string SoftwareName { get; init; }

    /// <summary>Emplacement d'origine, aujourd'hui une jonction.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Emplacement actuel des fichiers.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Identité du dossier de destination créé par le déplacement.</summary>
    public required FileIdentity DestinationIdentity { get; init; }

    /// <summary>Permissions d'origine de la racine (reposées telles quelles au retour). Facultatif.</summary>
    public byte[]? OriginalRootSecurity { get; init; }
}

/// <summary>
/// Nettoyage d'un déplacement devenu sans objet (logiciel désinstallé, ou dossier recréé à l'origine par une mise à
/// jour) : la copie restante sur le disque cible est supprimée et le déplacement retiré de la liste.
/// </summary>
public sealed record RetireRequest
{
    public required string MoveOperationId { get; init; }

    public required string SoftwareName { get; init; }

    /// <summary>Emplacement d'origine (absent, vrai dossier, ou encore la jonction).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Copie à supprimer.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Identité du dossier de destination créé par le déplacement : rien d'autre n'est supprimé.</summary>
    public required FileIdentity DestinationIdentity { get; init; }

    /// <summary>
    /// Si la jonction vers la copie est encore en place : la supprimer aussi (le logiciel ne fonctionnera plus).
    /// Sans cette autorisation explicite, le nettoyage est refusé dans ce cas.
    /// </summary>
    public bool RemoveActiveJunction { get; init; }
}

public enum OperationKind
{
    Move,
    Undo,
    Retire,
}

public enum OperationOutcome
{
    /// <summary>Terminé et vérifié.</summary>
    Completed,

    /// <summary>Terminé et vérifié ; l'ancienne copie n'a pas pu être entièrement supprimée (nettoyage reproposé).</summary>
    CompletedWithPendingCleanup,

    /// <summary>Annulé automatiquement : tout est revenu à l'état initial.</summary>
    RolledBack,

    /// <summary>Situation inattendue : rien d'irréversible n'a été fait, une décision humaine est requise.</summary>
    NeedsAttention,
}

public sealed record OperationResult(string OperationId, OperationKind Kind, OperationOutcome Outcome, string Message, IReadOnlyList<string> Warnings, Exception? Error = null)
{
    public bool Succeeded => Outcome is OperationOutcome.Completed or OperationOutcome.CompletedWithPendingCleanup;
}

/// <summary>Avancement d'une opération : étape en cours et, pendant un transfert, le détail.</summary>
public sealed record OperationProgress(string Step, TransferProgress? Transfer = null);

/// <summary>Points d'accroche système autour de la section critique (implémentés par l'application).</summary>
public interface IMoveHooks
{
    /// <summary>Dernière vérification juste avant le verrou (processus, services). Renvoie la raison d'un refus, ou null.</summary>
    string? CheckBeforeLock(string folderToLock);

    /// <summary>Début de la section critique : anti-veille, blocage de l'arrêt, arrêt de l'Explorateur si demandé.</summary>
    void EnterCriticalSection();

    /// <summary>Fin de la section critique, toujours appelée (succès, rollback ou erreur).</summary>
    void LeaveCriticalSection();
}

public sealed class NoMoveHooks : IMoveHooks
{
    public static NoMoveHooks Instance { get; } = new();

    public string? CheckBeforeLock(string folderToLock) => null;

    public void EnterCriticalSection()
    {
    }

    public void LeaveCriticalSection()
    {
    }
}

/// <summary>Fabrique du périmètre autorisé d'une opération.</summary>
public interface IPathScopeFactory
{
    PathScope Create(IFileSystem inner, IEnumerable<string> writableRoots);
}

/// <summary>Périmètre avec listes de chemins protégés fixes (et bac à sable facultatif pour les tests).</summary>
public sealed class PathScopeFactory : IPathScopeFactory
{
    public PathScopeFactory(IReadOnlyList<string> protectedExact, IReadOnlyList<string> protectedSubtrees, string? sandboxRoot = null, IReadOnlyList<string>? additionalSandboxRoots = null)
    {
        ProtectedExact = protectedExact;
        ProtectedSubtrees = protectedSubtrees;
        SandboxRoot = sandboxRoot;
        AdditionalSandboxRoots = additionalSandboxRoots ?? [];
    }

    public IReadOnlyList<string> ProtectedExact { get; }

    public IReadOnlyList<string> ProtectedSubtrees { get; }

    public string? SandboxRoot { get; }

    public IReadOnlyList<string> AdditionalSandboxRoots { get; }

    public PathScope Create(IFileSystem inner, IEnumerable<string> writableRoots)
        => PathScope.Create(inner, writableRoots, ProtectedExact, ProtectedSubtrees, SandboxRoot, AdditionalSandboxRoots);
}

/// <summary>Dépendances du moteur de déplacement.</summary>
public sealed class MoveEngineContext
{
    /// <summary>Système de fichiers BRUT : le moteur l'enveloppe lui-même d'une garde de périmètre par opération.</summary>
    public required IFileSystem FileSystem { get; init; }

    public required IPathScopeFactory ScopeFactory { get; init; }

    public required OperationStore Store { get; init; }

    public IMoveHooks Hooks { get; init; } = NoMoveHooks.Instance;

    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    public Func<string> NewOperationId { get; init; } = OperationIds.New;
}

/// <summary>Identifiants d'opération et noms des dossiers temporaires.</summary>
public static class OperationIds
{
    public static string New() => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];

    /// <summary>Suffixe court, unique par opération.</summary>
    public static string ShortId(string operationId) => operationId[^8..];

    public static bool IsValid(string operationId)
        => operationId.Length is >= 10 and <= 64 && operationId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Dossier verrouillé (renommé) dans le même parent : <c>Nom.dmv-old-xxxxxxxx</c>.</summary>
    public static string LockedPath(string path, string operationId)
        => PathUtil.Join(PathUtil.GetParent(path)!, PathUtil.GetName(path) + ".dmv-old-" + ShortId(operationId));

    /// <summary>Copie mise de côté avant suppression (nettoyage) : <c>Nom.dmv-suppr-xxxxxxxx</c>.</summary>
    public static string RetiringPath(string path, string operationId)
        => PathUtil.Join(PathUtil.GetParent(path)!, PathUtil.GetName(path) + ".dmv-suppr-" + ShortId(operationId));

    /// <summary>Copie de retour (annulation) dans le même parent : <c>Nom.dmv-retour-xxxxxxxx</c>.</summary>
    public static string RestorePath(string path, string operationId)
        => PathUtil.Join(PathUtil.GetParent(path)!, PathUtil.GetName(path) + ".dmv-retour-" + ShortId(operationId));
}

/// <summary>Arrêt volontaire d'une opération avant le point de non-retour (déclenche le rollback).</summary>
public sealed class OperationAbortedException : Exception
{
    public OperationAbortedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
