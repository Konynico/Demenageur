using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>
/// Reprise des opérations interrompues (crash, coupure de courant), au lancement suivant de l'application.
/// L'état réel du disque fait foi :
/// <list type="bullet">
/// <item>déplacement après le point de non-retour → fin du nettoyage ;</item>
/// <item>jonction en place et copie déjà vérifiée → nouvelle vérification complète, puis fin ;</item>
/// <item>sinon → rollback ;</item>
/// <item>journal illisible ou non fiable → aucune action automatique.</item>
/// </list>
/// La reprise est idempotente : elle peut elle-même être interrompue et relancée.
/// </summary>
public sealed class RecoveryService
{
    private readonly MoveEngineContext _context;

    public RecoveryService(MoveEngineContext context)
    {
        _context = context;
    }

    /// <summary>Opérations non terminées (y compris celles qui attendent une décision ou un nettoyage).</summary>
    public IReadOnlyList<StoredOperation> PendingOperations()
        => _context.Store.List().Where(o => !o.IsTerminal && !o.IsEmpty).ToList();

    public OperationResult Recover(StoredOperation operation, CancellationToken cancellationToken = default)
    {
        if (!operation.Trusted)
        {
            return Result(operation, OperationOutcome.NeedsAttention, "Journal ignoré : il n'appartient pas à un administrateur (dépôt suspect).");
        }

        if (!operation.IsReadable)
        {
            return Result(operation, OperationOutcome.NeedsAttention, "Journal illisible ou corrompu : aucune action automatique. Consulte la procédure manuelle dans l'aide.");
        }

        var raw = _context.FileSystem;
        var writable = new List<string> { operation.SourcePath!, operation.DestinationPath!, operation.LockedPath!, operation.Directory };
        if (operation.RestorePath is { } restorePath)
        {
            writable.Add(restorePath);
        }

        IFileSystem fs = new PathGuard(raw, _context.ScopeFactory.Create(raw, writable));
        var journal = new OperationJournal(fs, PathUtil.Join(operation.Directory, OperationStore.JournalFileName), _context.Clock, operation.Journal.Last?.Sequence ?? 0);
        MoveProcedures.TryAppend(journal, JournalSteps.Info, "Reprise après interruption.");

        _context.Hooks.EnterCriticalSection();
        try
        {
            return operation.Kind switch
            {
                OperationKind.Move => RecoverMove(operation, fs, journal, cancellationToken),
                OperationKind.Undo => RecoverUndo(operation, fs, journal),
                _ => RecoverRetire(operation, fs, journal, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException or FormatException)
        {
            MoveProcedures.TryAppend(journal, JournalSteps.NeedsAttention, "Reprise impossible : " + ex.Message);
            return Result(operation, OperationOutcome.NeedsAttention, "Reprise impossible : " + ex.Message, ex);
        }
        finally
        {
            _context.Hooks.LeaveCriticalSection();
        }
    }

    private OperationResult RecoverMove(StoredOperation operation, IFileSystem fs, OperationJournal journal, CancellationToken cancellationToken)
    {
        var facts = new MoveFacts(operation.SourcePath!, operation.DestinationPath!, operation.LockedPath!)
        {
            SourceId = operation.IdentityFrom(JournalSteps.LockIntent, JournalKeys.SourceId),
            DestinationId = operation.IdentityFrom(JournalSteps.DestinationCreated, JournalKeys.DestinationId),
            JunctionId = operation.IdentityFrom(JournalSteps.JunctionCreated, JournalKeys.JunctionId),
            DestinationIntent = operation.Journal.Has(JournalSteps.DestinationIntent),
        };

        if (operation.IsCommitted)
        {
            var manifest = _context.Store.LoadManifest(operation.Id, OperationStore.ManifestFileName)
                           ?? throw new FormatException("Manifeste de l'original introuvable.");
            var outcome = MoveProcedures.FinishMove(fs, journal, facts, manifest, _context.Retry, out var message);
            return Result(operation, outcome, message);
        }

        // Jonction déjà en place et copie déjà vérifiée : on revérifie tout avant de terminer.
        var atSource = fs.GetEntry(facts.Source);
        var verifiedManifest = operation.Journal.Has(JournalSteps.SecurityVerified)
            ? _context.Store.LoadManifest(operation.Id, OperationStore.VerifiedManifestFileName)
            : null;
        if (atSource is { Kind: FsEntryKind.Link }
            && verifiedManifest is not null
            && MoveProcedures.IsJunctionTo(fs, facts.Source, atSource.Identity, facts.Destination))
        {
            var verifier = new TreeVerifier(fs);
            var content = verifier.Verify(verifiedManifest, facts.Destination, new VerificationOptions { CompareSecurity = true }, cancellationToken: cancellationToken);
            var through = verifier.Verify(verifiedManifest, facts.Source, new VerificationOptions { FollowRootLink = true, CompareContent = false }, cancellationToken: cancellationToken);
            if (content.Success && through.Success)
            {
                journal.Append(JournalSteps.Committed, "Point de non-retour (après reprise et nouvelle vérification complète).");
                var outcome = MoveProcedures.FinishMove(fs, journal, facts, verifiedManifest, _context.Retry, out var message);
                return Result(operation, outcome, message);
            }

            MoveProcedures.TryAppend(journal, JournalSteps.Info, "La nouvelle vérification a échoué : retour à l'état initial.");
        }

        var rollback = MoveProcedures.RollbackMove(fs, journal, facts, _context.Retry);
        return Result(operation, rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention, rollback.Message);
    }

    private OperationResult RecoverUndo(StoredOperation operation, IFileSystem fs, OperationJournal journal)
    {
        var destinationId = operation.Header?.Get(JournalKeys.DestinationId) is { } text
            ? FileIdentity.Parse(text)
            : throw new FormatException("Identité de la destination absente du journal.");
        var facts = new UndoFacts(operation.SourcePath!, operation.DestinationPath!, operation.LockedPath!, operation.RestorePath ?? throw new FormatException("Chemin de retour absent du journal."), destinationId)
        {
            RestoreId = operation.IdentityFrom(JournalSteps.DestinationCreated, JournalKeys.RestoreId),
            RestoreIntent = operation.Journal.Has(JournalSteps.DestinationIntent),
        };

        if (operation.IsCommitted)
        {
            var manifest = _context.Store.LoadManifest(operation.Id, OperationStore.ManifestFileName)
                           ?? throw new FormatException("Manifeste introuvable.");
            var outcome = UndoProcedures.Finish(fs, journal, facts, manifest, _context.Retry, out var message);
            return Result(operation, outcome, message);
        }

        var rollback = UndoProcedures.Rollback(fs, journal, facts, _context.Retry);
        return Result(operation, rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention, rollback.Message);
    }

    private OperationResult RecoverRetire(StoredOperation operation, IFileSystem fs, OperationJournal journal, CancellationToken cancellationToken)
    {
        var destinationId = operation.Header?.Get(JournalKeys.DestinationId) is { } text
            ? FileIdentity.Parse(text)
            : throw new FormatException("Identité de la copie absente du journal.");
        var facts = new RetireFacts(operation.SourcePath!, operation.DestinationPath!, operation.LockedPath!, destinationId,
            operation.Header?.Get(JournalKeys.RemoveJunction) == "true");

        if (operation.IsCommitted)
        {
            var outcome = RetireProcedures.Finish(fs, journal, facts, _context.Retry, out var message, cancellationToken);
            return Result(operation, outcome, message);
        }

        var junctionWasActive = operation.Journal.LastOf(JournalSteps.Locked)?.Get(JournalKeys.RemoveJunction) == "true";
        var rollback = RetireProcedures.Rollback(fs, journal, facts, junctionWasActive, _context.Retry);
        return Result(operation, rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention, rollback.Message);
    }

    private static OperationResult Result(StoredOperation operation, OperationOutcome outcome, string message, Exception? error = null)
        => new(operation.Id, operation.Kind, outcome, message, [], error);
}
