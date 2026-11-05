using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>Ce que l'on sait d'une annulation (en mémoire ou relu du journal).</summary>
internal sealed class UndoFacts
{
    public UndoFacts(string source, string destination, string locked, string restore, FileIdentity destinationId)
    {
        Source = source;
        Destination = destination;
        Locked = locked;
        Restore = restore;
        DestinationId = destinationId;
    }

    /// <summary>S : emplacement d'origine (la jonction).</summary>
    public string Source { get; }

    /// <summary>T : emplacement actuel des fichiers.</summary>
    public string Destination { get; }

    /// <summary>T′ : T renommé (verrou).</summary>
    public string Locked { get; }

    /// <summary>S″ : copie de retour, à côté de la jonction.</summary>
    public string Restore { get; }

    public FileIdentity DestinationId { get; }

    public FileIdentity? RestoreId { get; set; }

    public bool RestoreIntent { get; set; }
}

/// <summary>
/// Annulation d'un déplacement (« Annuler le déplacement »), même invariant que le déplacement :
/// <list type="number">
/// <item>vérifications : la jonction est la nôtre, la copie est celle créée par le déplacement ;</item>
/// <item>T renommé en T′ (verrou) ;</item>
/// <item>copie T′ → S″ à côté de la jonction, vérification, permissions d'origine ;</item>
/// <item>jonction supprimée (le lien seul), S″ renommé en S, vérification → POINT DE NON-RETOUR ;</item>
/// <item>suppression de T′ d'après son manifeste.</item>
/// </list>
/// La copie de retour reprend l'état ACTUEL (mises à jour faites depuis le déplacement comprises).
/// </summary>
public sealed class UndoTransaction
{
    private readonly MoveEngineContext _context;

    public UndoTransaction(MoveEngineContext context)
    {
        _context = context;
    }

    public OperationResult Execute(UndoRequest request, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var operationId = _context.NewOperationId();
        var source = PathUtil.TrimTrailingSeparator(request.SourcePath);
        var destination = PathUtil.TrimTrailingSeparator(request.DestinationPath);
        var locked = OperationIds.LockedPath(destination, operationId);
        var restore = OperationIds.RestorePath(source, operationId);
        var operationDirectory = _context.Store.DirectoryOf(operationId);
        var warnings = new List<string>();
        var raw = _context.FileSystem;
        var retry = _context.Retry;

        IFileSystem fs;
        OperationJournal journal;
        try
        {
            fs = new PathGuard(raw, _context.ScopeFactory.Create(raw, [source, restore, destination, locked, operationDirectory]));
            fs.CreateDirectory(operationDirectory);
            journal = new OperationJournal(fs, PathUtil.Join(operationDirectory, OperationStore.JournalFileName), _context.Clock);
            journal.Append(JournalSteps.Planned, $"Annulation du déplacement de « {request.SoftwareName} ».", new Dictionary<string, string>
            {
                [JournalKeys.Kind] = nameof(OperationKind.Undo),
                [JournalKeys.MoveOperation] = request.MoveOperationId,
                [JournalKeys.Software] = request.SoftwareName,
                [JournalKeys.Source] = source,
                [JournalKeys.Destination] = destination,
                [JournalKeys.Locked] = locked,
                [JournalKeys.Restore] = restore,
                [JournalKeys.DestinationId] = request.DestinationIdentity.ToString(),
            });
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            return new OperationResult(operationId, OperationKind.Undo, OperationOutcome.RolledBack, $"Préparation impossible, rien n'a été modifié : {ex.Message}", warnings, ex);
        }

        var facts = new UndoFacts(source, destination, locked, restore, request.DestinationIdentity);
        Manifest? manifest = null;
        void Report(string step, TransferProgress? transfer = null) => progress?.Invoke(new OperationProgress(step, transfer));

        _context.Hooks.EnterCriticalSection();
        try
        {
            // ── 1) Vérifications ──
            var junction = fs.GetEntry(source);
            if (junction is not { Kind: FsEntryKind.Link } || !MoveProcedures.IsJunctionTo(fs, source, junction.Identity, destination))
            {
                throw new OperationAbortedException($"« {source} » n'est plus la jonction créée par Déménageur vers « {destination} ».");
            }

            var current = fs.GetEntry(destination);
            if (current is not { Kind: FsEntryKind.Directory } || current.Identity != request.DestinationIdentity)
            {
                throw new OperationAbortedException($"« {destination} » n'est plus le dossier créé par le déplacement : annulation refusée.");
            }

            if (fs.GetEntry(locked) is not null || fs.GetEntry(restore) is not null)
            {
                throw new OperationAbortedException("Des dossiers temporaires d'une autre opération sont présents.");
            }

            Report("Vérification des processus");
            // Un programme lancé via la jonction apparaît avec son chemin réel : on vérifie les deux chemins.
            var refusal = _context.Hooks.CheckBeforeLock(destination) ?? _context.Hooks.CheckBeforeLock(source);
            if (refusal is not null)
            {
                throw new OperationAbortedException(refusal);
            }

            // ── 2) Verrou de la copie actuelle ──
            Report("Verrouillage des fichiers déplacés");
            journal.Append(JournalSteps.LockIntent);
            try
            {
                retry.Execute(() => fs.Rename(destination, request.DestinationIdentity, PathUtil.GetName(locked)), cancellationToken);
            }
            catch (FileSystemException ex) when (ex.Kind is FsErrorKind.AccessDenied or FsErrorKind.SharingViolation)
            {
                throw new OperationAbortedException("Un fichier du logiciel est encore ouvert : annulation impossible pour l'instant. Rien n'a été modifié.", ex);
            }

            journal.Append(JournalSteps.Locked, $"Fichiers déplacés verrouillés sous « {locked} ».");

            // ── 3) Copie de retour ──
            Report("Inventaire des fichiers");
            var snapshot = new TreeScanner(fs).Scan(locked, ScanOptions.Full, cancellationToken: cancellationToken);
            if (snapshot.HasBlockingProblems)
            {
                throw new OperationAbortedException(snapshot.Problems.First(p => p.Severity == ScanProblemSeverity.Blocking).Message);
            }

            manifest = Manifest.FromSnapshot(snapshot);
            fs.WriteNewFile(PathUtil.Join(operationDirectory, OperationStore.ManifestFileName), ManifestSerializer.Serialize(manifest));
            journal.Append(JournalSteps.ManifestSaved);

            Report("Copie de retour");
            facts.RestoreIntent = true;
            journal.Append(JournalSteps.DestinationIntent);
            facts.RestoreId = fs.CreateDirectory(restore, SecurityDescriptorTools.CreatePrivateDirectoryDescriptor());
            journal.Append(JournalSteps.DestinationCreated, null, new Dictionary<string, string> { [JournalKeys.RestoreId] = facts.RestoreId.Value.ToString() });

            var copier = new TreeCopier(fs, retry);
            copier.Copy(manifest, restore, facts.RestoreId.Value, p => Report("Copie de retour", p), cancellationToken);
            journal.Append(JournalSteps.Copied);

            Report("Vérification du contenu");
            var verification = new TreeVerifier(fs).Verify(manifest, restore, new VerificationOptions(), p => Report("Vérification du contenu", p), cancellationToken);
            if (!verification.Success)
            {
                throw new OperationAbortedException(verification.Summary);
            }

            journal.Append(JournalSteps.ContentVerified, verification.Summary);
            Report("Permissions");
            copier.ApplySecurity(manifest, restore, request.OriginalRootSecurity, cancellationToken);
            var securityCheck = new TreeVerifier(fs).Verify(manifest, restore, new VerificationOptions
            {
                CompareContent = false,
                CompareSecurity = true,
                ExpectedRootSecurity = request.OriginalRootSecurity,
            }, cancellationToken: cancellationToken);
            if (!securityCheck.Success)
            {
                throw new OperationAbortedException(securityCheck.Summary);
            }

            warnings.AddRange(copier.Warnings.Select(w => $"{w.RelativePath} : {w.Message}"));
            journal.Append(JournalSteps.SecurityVerified);

            // ── 4) Remplacement de la jonction par la copie de retour ──
            Report("Remise en place");
            cancellationToken.ThrowIfCancellationRequested();
            journal.Append(JournalSteps.RestoreIntent);
            retry.Execute(() => fs.Delete(source, junction.Identity));
            journal.Append(JournalSteps.JunctionRemoved);
            retry.Execute(() => fs.Rename(restore, facts.RestoreId.Value, PathUtil.GetName(source)));
            journal.Append(JournalSteps.Restored);

            var final = new TreeVerifier(fs).Verify(manifest, source, new VerificationOptions { CompareContent = false }, cancellationToken: cancellationToken);
            if (!final.Success)
            {
                throw new OperationAbortedException("Vérification après remise en place incorrecte : " + final.Summary);
            }

            if (!CommitPoint.TryCommit(fs, journal, "Point de non-retour : le logiciel fonctionne de nouveau depuis son emplacement d'origine.", out var undecided))
            {
                _context.Hooks.LeaveCriticalSection();
                return new OperationResult(operationId, OperationKind.Undo, OperationOutcome.NeedsAttention,
                    "Le logiciel est revenu à son emplacement d'origine, mais le journal n'a pas pu être mis à jour : la reprise terminera l'opération au prochain lancement.",
                    warnings, undecided);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Hooks.LeaveCriticalSection();
            var reason = ex is OperationCanceledException ? "Opération annulée." : ex.Message;
            MoveProcedures.TryAppend(journal, JournalSteps.Info, "Erreur avant le point de non-retour : " + reason);
            var rollback = UndoProcedures.Rollback(fs, journal, facts, retry);
            return new OperationResult(operationId, OperationKind.Undo,
                rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention,
                $"{reason} {rollback.Message}", warnings, ex);
        }

        _context.Hooks.LeaveCriticalSection();

        // ── 5) Suppression de la copie qui était sur le disque cible ──
        Report("Suppression de l'ancienne copie");
        var outcome = UndoProcedures.Finish(fs, journal, facts, manifest!, retry, out var message);
        return new OperationResult(operationId, OperationKind.Undo, outcome, message, warnings);
    }
}

internal static class UndoProcedures
{
    public static RollbackReport Rollback(IFileSystem fs, OperationJournal? journal, UndoFacts facts, RetryPolicy retry)
    {
        MoveProcedures.TryAppend(journal, JournalSteps.RollbackStarted, "Retour à l'état « déplacé ».");

        // 0) Verrou jamais posé (T à sa place, ni T′ ni S″) : rien n'a été touché, en particulier pas S.
        var destinationNow = fs.GetEntry(facts.Destination);
        if (destinationNow is not null && destinationNow.Identity == facts.DestinationId
            && fs.GetEntry(facts.Locked) is null && fs.GetEntry(facts.Restore) is null)
        {
            MoveProcedures.TryAppend(journal, JournalSteps.RolledBack, "Rien n'a été modifié.");
            return new RollbackReport(true, "Rien n'a été modifié.");
        }

        // 1) Si la copie de retour a déjà pris la place de la jonction, on la remet de côté.
        var atSource = fs.GetEntry(facts.Source);
        if (atSource is { Kind: FsEntryKind.Directory })
        {
            if (facts.RestoreId is { } restoreId && atSource.Identity == restoreId)
            {
                retry.Execute(() => fs.Rename(facts.Source, restoreId, PathUtil.GetName(facts.Restore)));
                atSource = null;
            }
            else
            {
                return Attention(journal, $"Un dossier inconnu occupe « {facts.Source} » : rien n'a été supprimé.");
            }
        }

        // 2) Lever le verrou : la copie déplacée reprend son nom.
        var destination = fs.GetEntry(facts.Destination);
        if (destination is null)
        {
            var locked = fs.GetEntry(facts.Locked);
            if (locked is not null && locked.Identity == facts.DestinationId)
            {
                retry.Execute(() => fs.Rename(facts.Locked, facts.DestinationId, PathUtil.GetName(facts.Destination)));
            }
            else
            {
                return Attention(journal, $"Les fichiers déplacés sont introuvables (ni « {facts.Destination} » ni « {facts.Locked} »).");
            }
        }
        else if (destination.Identity != facts.DestinationId)
        {
            return Attention(journal, $"« {facts.Destination} » n'est plus le dossier déplacé : rien n'a été supprimé.");
        }

        // 3) La jonction doit pointer vers la copie.
        atSource ??= fs.GetEntry(facts.Source);
        if (atSource is null)
        {
            retry.Execute(() => fs.CreateReparsePoint(facts.Source, isDirectory: true, ReparsePointData.CreateJunction(facts.Destination)));
        }
        else if (atSource.Kind != FsEntryKind.Link || !MoveProcedures.IsJunctionTo(fs, facts.Source, atSource.Identity, facts.Destination))
        {
            return Attention(journal, $"« {facts.Source} » n'est pas la jonction attendue.");
        }

        // 4) La copie de retour, devenue inutile, est supprimée (si c'est bien la nôtre).
        var restore = fs.GetEntry(facts.Restore);
        if (restore is not null)
        {
            if (facts.RestoreId is { } rid && restore.Identity == rid)
            {
                var deletion = new SafeDeleter(fs, retry).DeleteTree(facts.Restore, rid);
                if (!deletion.Complete)
                {
                    MoveProcedures.TryAppend(journal, JournalSteps.RolledBack, $"Logiciel de nouveau accessible via la jonction. Copie de retour partielle à supprimer : « {facts.Restore} ».");
                    return new RollbackReport(true, $"Le logiciel reste déplacé ; copie de retour partielle à supprimer : « {facts.Restore} ».");
                }
            }
            else if (facts.RestoreIntent && MoveProcedures.IsEmptyDirectory(fs, facts.Restore, restore))
            {
                retry.Execute(() => fs.Delete(facts.Restore, restore.Identity));
            }
        }

        MoveProcedures.TryAppend(journal, JournalSteps.RolledBack, "Le logiciel reste déplacé, comme avant l'annulation.");
        return new RollbackReport(true, "Le logiciel reste déplacé, comme avant l'annulation.");
    }

    public static OperationOutcome Finish(IFileSystem fs, OperationJournal journal, UndoFacts facts, Manifest manifest, RetryPolicy retry, out string message)
    {
        try
        {
            var deletion = new SafeDeleter(fs, retry).DeleteEntries(facts.Locked, manifest.Entries.Select(e => (e.RelativePath, e.SourceIdentity)).ToList());
            if (deletion.Complete)
            {
                journal.Append(JournalSteps.CleanupDone);
                journal.Append(JournalSteps.Completed, "Annulation terminée.");
                message = "Le logiciel est revenu à son emplacement d'origine.";
                return OperationOutcome.Completed;
            }

            message = $"Le logiciel est revenu à son emplacement d'origine. « {facts.Locked} » n'a pas pu être entièrement supprimé : nouvel essai au prochain lancement.";
            journal.Append(JournalSteps.CleanupPending, message);
            return OperationOutcome.CompletedWithPendingCleanup;
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            message = $"Le logiciel est revenu à son emplacement d'origine. Nettoyage de « {facts.Locked} » interrompu : {ex.Message}";
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
