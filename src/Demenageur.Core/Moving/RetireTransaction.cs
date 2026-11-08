using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>Titres des étapes d'un nettoyage (partagés par la simulation et l'exécution).</summary>
public static class RetireSteps
{
    public const string Checks = "Vérifications";
    public const string Lock = "Verrouillage de la copie";
    public const string RemoveJunction = "Suppression de la jonction";
    public const string Delete = "Suppression de la copie";
}

/// <summary>Ce que l'on sait d'un nettoyage (en mémoire ou relu du journal).</summary>
internal sealed class RetireFacts
{
    public RetireFacts(string source, string destination, string retiring, FileIdentity destinationId, bool removeJunction)
    {
        Source = source;
        Destination = destination;
        Retiring = retiring;
        DestinationId = destinationId;
        RemoveJunction = removeJunction;
    }

    /// <summary>S : emplacement d'origine.</summary>
    public string Source { get; }

    /// <summary>T : la copie créée par le déplacement.</summary>
    public string Destination { get; }

    /// <summary>T renommé (verrou) avant suppression.</summary>
    public string Retiring { get; }

    public FileIdentity DestinationId { get; }

    /// <summary>La jonction S → T était encore en place et l'utilisateur a accepté de la supprimer.</summary>
    public bool RemoveJunction { get; }
}

/// <summary>
/// Nettoyage d'un déplacement devenu sans objet : le logiciel a été désinstallé (la jonction a disparu ou ne mène
/// plus qu'à des restes), ou une mise à jour a recréé un vrai dossier à l'emplacement d'origine.
/// <list type="number">
/// <item>vérifications : la copie est bien celle créée par le déplacement (identité), aucun processus ne l'utilise ;
/// si la jonction S → T est encore en place, il faut l'autorisation explicite de la supprimer ;</item>
/// <item>T renommé en T′ (verrou : échoue si un fichier est ouvert) ;</item>
/// <item>jonction supprimée (le lien seul), si autorisé → POINT DE NON-RETOUR ;</item>
/// <item>suppression de T′ élément par élément (jamais à travers un lien), reprise au lancement suivant si interrompue.</item>
/// </list>
/// Rien n'est jamais supprimé à l'emplacement d'origine, à part la jonction quand elle est explicitement autorisée.
/// </summary>
public sealed class RetireTransaction
{
    private readonly MoveEngineContext _context;

    public RetireTransaction(MoveEngineContext context)
    {
        _context = context;
    }

    public OperationResult Execute(RetireRequest request, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var operationId = _context.NewOperationId();
        var source = PathUtil.TrimTrailingSeparator(request.SourcePath);
        var destination = PathUtil.TrimTrailingSeparator(request.DestinationPath);
        var retiring = OperationIds.RetiringPath(destination, operationId);
        var operationDirectory = _context.Store.DirectoryOf(operationId);
        var raw = _context.FileSystem;
        var retry = _context.Retry;
        void Report(string step) => progress?.Invoke(new OperationProgress(step));

        IFileSystem fs;
        OperationJournal journal;
        try
        {
            // La source n'est modifiable que si la suppression de la jonction est autorisée.
            var writable = new List<string> { destination, retiring, operationDirectory };
            if (request.RemoveActiveJunction)
            {
                writable.Add(source);
            }

            fs = new PathGuard(raw, _context.ScopeFactory.Create(raw, writable));
            fs.CreateDirectory(operationDirectory);
            journal = new OperationJournal(fs, PathUtil.Join(operationDirectory, OperationStore.JournalFileName), _context.Clock);
            journal.Append(JournalSteps.Planned, $"Nettoyage du déplacement de « {request.SoftwareName} ».", new Dictionary<string, string>
            {
                [JournalKeys.Kind] = nameof(OperationKind.Retire),
                [JournalKeys.MoveOperation] = request.MoveOperationId,
                [JournalKeys.Software] = request.SoftwareName,
                [JournalKeys.Source] = source,
                [JournalKeys.Destination] = destination,
                [JournalKeys.Locked] = retiring,
                [JournalKeys.DestinationId] = request.DestinationIdentity.ToString(),
                [JournalKeys.RemoveJunction] = request.RemoveActiveJunction ? "true" : "false",
            });
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            return Result(operationId, OperationOutcome.RolledBack, $"Préparation impossible, rien n'a été modifié : {ex.Message}", ex);
        }

        var facts = new RetireFacts(source, destination, retiring, request.DestinationIdentity, request.RemoveActiveJunction);
        FsEntryInfo? junction = null;
        try
        {
            // Dans le try : si l'entrée échoue (service non arrêtable, logiciel encore ouvert…), rollback et sortie propres.
            _context.Hooks.EnterCriticalSection();

            // ── 1) Vérifications ──
            Report(RetireSteps.Checks);
            var atSource = fs.GetEntry(source);
            var junctionActive = atSource is { Kind: FsEntryKind.Link } && MoveProcedures.IsJunctionTo(fs, source, atSource.Identity, destination);
            if (junctionActive && !request.RemoveActiveJunction)
            {
                throw new OperationAbortedException($"La jonction « {source} » mène toujours à la copie : le logiciel est peut-être encore installé. Nettoyage refusé sans autorisation explicite. Rien n'a été modifié.");
            }

            if (fs.GetEntry(retiring) is not null)
            {
                throw new OperationAbortedException("Des dossiers temporaires d'une autre opération sont présents.");
            }

            var current = fs.GetEntry(destination);
            if (current is null)
            {
                if (junctionActive)
                {
                    // Disque débranché ? On ne touche pas à la jonction : elle refonctionnera au retour du disque.
                    throw new OperationAbortedException($"La copie « {destination} » est introuvable alors que la jonction est en place (disque débranché ?). Rien n'a été modifié.");
                }

                journal.Append(JournalSteps.Committed, "La copie n'existe plus : rien à supprimer.");
                journal.Append(JournalSteps.Completed, "Déplacement retiré de la liste.");
                _context.Hooks.LeaveCriticalSection();
                return Result(operationId, OperationOutcome.Completed, "La copie n'existait déjà plus : le déplacement est retiré de la liste.");
            }

            if (current.Kind != FsEntryKind.Directory || current.Identity != request.DestinationIdentity)
            {
                throw new OperationAbortedException($"« {destination} » n'est plus le dossier créé par le déplacement : nettoyage refusé. Rien n'a été modifié.");
            }

            var refusal = _context.Hooks.CheckBeforeLock(destination) ?? (junctionActive ? _context.Hooks.CheckBeforeLock(source) : null);
            if (refusal is not null)
            {
                throw new OperationAbortedException(refusal);
            }

            // ── 2) Verrou ──
            Report(RetireSteps.Lock);
            journal.Append(JournalSteps.LockIntent);
            try
            {
                retry.Execute(() => fs.Rename(destination, request.DestinationIdentity, PathUtil.GetName(retiring)), cancellationToken);
            }
            catch (FileSystemException ex) when (ex.Kind is FsErrorKind.AccessDenied or FsErrorKind.SharingViolation)
            {
                throw new OperationAbortedException("Un fichier de la copie est encore ouvert : nettoyage impossible pour l'instant. Rien n'a été modifié.", ex);
            }

            junction = junctionActive ? atSource : null;
            journal.Append(JournalSteps.Locked, $"Copie mise de côté sous « {retiring} ».", new Dictionary<string, string>
            {
                [JournalKeys.RemoveJunction] = junctionActive ? "true" : "false",
            });

            // ── 3) Jonction (le lien seul), si autorisé ──
            if (junctionActive)
            {
                Report(RetireSteps.RemoveJunction);
                cancellationToken.ThrowIfCancellationRequested();
                retry.Execute(() => fs.Delete(source, atSource!.Identity));
                journal.Append(JournalSteps.JunctionRemoved);
            }

            if (!CommitPoint.TryCommit(fs, journal, "Point de non-retour : suppression de la copie.", out var undecided))
            {
                _context.Hooks.LeaveCriticalSection();
                return Result(operationId, OperationOutcome.NeedsAttention,
                    "Le journal n'a pas pu être mis à jour : la reprise terminera le nettoyage au prochain lancement.", undecided);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Hooks.LeaveCriticalSection();
            var reason = ex is OperationCanceledException ? "Opération annulée." : ex.Message;
            MoveProcedures.TryAppend(journal, JournalSteps.Info, "Erreur avant le point de non-retour : " + reason);
            var rollback = RetireProcedures.Rollback(fs, journal, facts, junction is not null, retry);
            return Result(operationId, rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention, $"{reason} {rollback.Message}".Trim(), ex);
        }

        _context.Hooks.LeaveCriticalSection();

        // ── 4) Suppression ──
        Report(RetireSteps.Delete);
        var outcome = RetireProcedures.Finish(fs, journal, facts, retry, out var message, cancellationToken);
        return Result(operationId, outcome, message);
    }

    private static OperationResult Result(string operationId, OperationOutcome outcome, string message, Exception? error = null)
        => new(operationId, OperationKind.Retire, outcome, message, [], error);
}

internal static class RetireProcedures
{
    /// <summary>
    /// Avant le point de non-retour : la copie reprend son nom ; la jonction est recréée si elle était en place au
    /// moment du verrou et a disparu depuis.
    /// </summary>
    public static RollbackReport Rollback(IFileSystem fs, OperationJournal? journal, RetireFacts facts, bool junctionWasActive, RetryPolicy retry)
    {
        MoveProcedures.TryAppend(journal, JournalSteps.RollbackStarted);

        // 1) Le verrou a-t-il été posé ? Si oui, la copie reprend son nom.
        var retiring = fs.GetEntry(facts.Retiring);
        if (retiring is not null)
        {
            if (retiring.Identity != facts.DestinationId)
            {
                return Attention(journal, $"« {facts.Retiring} » n'est pas la copie attendue : rien n'a été supprimé.");
            }

            if (fs.GetEntry(facts.Destination) is not null)
            {
                return Attention(journal, $"« {facts.Destination} » a été recréé pendant le nettoyage : rien n'a été supprimé, la copie reste sous « {facts.Retiring} ».");
            }

            retry.Execute(() => fs.Rename(facts.Retiring, facts.DestinationId, PathUtil.GetName(facts.Destination)));
        }

        // 2) La jonction, si elle était en place au moment du verrou et a disparu depuis, est recréée vers la copie.
        if (junctionWasActive && facts.RemoveJunction && fs.GetEntry(facts.Source) is null
            && fs.GetEntry(facts.Destination) is { } copy && copy.Identity == facts.DestinationId)
        {
            retry.Execute(() => fs.CreateReparsePoint(facts.Source, isDirectory: true, ReparsePointData.CreateJunction(facts.Destination)));
        }

        MoveProcedures.TryAppend(journal, JournalSteps.RolledBack, "Rien n'a été supprimé.");
        return new RollbackReport(true, "Rien n'a été supprimé.");
    }

    /// <summary>Après le point de non-retour : suppression de la copie mise de côté (idempotent).</summary>
    public static OperationOutcome Finish(IFileSystem fs, OperationJournal journal, RetireFacts facts, RetryPolicy retry, out string message, CancellationToken cancellationToken = default)
    {
        try
        {
            // Si le crash a eu lieu entre le verrou et la suppression de la jonction, on la termine ici.
            if (facts.RemoveJunction && fs.GetEntry(facts.Source) is { Kind: FsEntryKind.Link } link
                && MoveProcedures.IsJunctionTo(fs, facts.Source, link.Identity, facts.Destination))
            {
                retry.Execute(() => fs.Delete(facts.Source, link.Identity), cancellationToken);
                journal.Append(JournalSteps.JunctionRemoved);
            }

            var deletion = new SafeDeleter(fs, retry).DeleteTree(facts.Retiring, facts.DestinationId, cancellationToken);
            if (deletion.Complete)
            {
                journal.Append(JournalSteps.CleanupDone, $"{deletion.Deleted} élément(s) supprimé(s).");
                journal.Append(JournalSteps.Completed, "Nettoyage terminé.");
                message = "La copie restante a été supprimée et le déplacement retiré de la liste.";
                return OperationOutcome.Completed;
            }

            message = $"La copie n'a pas pu être entièrement supprimée ({deletion.Failures.Count} élément(s) restant(s), sous « {facts.Retiring} ») : nouvel essai au prochain lancement. Première erreur : {deletion.Failures[0].Message}";
            journal.Append(JournalSteps.CleanupPending, message);
            return OperationOutcome.CompletedWithPendingCleanup;
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            message = $"Nettoyage de « {facts.Retiring} » interrompu : {ex.Message}. Nouvel essai au prochain lancement.";
            MoveProcedures.TryAppend(journal, JournalSteps.CleanupPending, message);
            return OperationOutcome.CompletedWithPendingCleanup;
        }
    }

    private static RollbackReport Attention(OperationJournal? journal, string message)
    {
        MoveProcedures.TryAppend(journal, JournalSteps.NeedsAttention, message);
        return new RollbackReport(false, message);
    }
}
