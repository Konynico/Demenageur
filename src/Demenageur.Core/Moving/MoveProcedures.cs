using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>Ce que l'on sait d'un déplacement (en mémoire ou relu du journal).</summary>
internal sealed class MoveFacts
{
    public MoveFacts(string source, string destination, string locked)
    {
        Source = source;
        Destination = destination;
        Locked = locked;
    }

    /// <summary>S : emplacement d'origine.</summary>
    public string Source { get; }

    /// <summary>T : destination.</summary>
    public string Destination { get; }

    /// <summary>S′ : original renommé (verrou).</summary>
    public string Locked { get; }

    public FileIdentity? SourceId { get; set; }

    public FileIdentity? DestinationId { get; set; }

    public FileIdentity? JunctionId { get; set; }

    public bool DestinationIntent { get; set; }
}

/// <summary>Résultat d'un rollback.</summary>
internal sealed record RollbackReport(bool Success, string Message);

/// <summary>
/// Procédures partagées par la transaction et la reprise après crash. Le rollback est piloté par l'ÉTAT RÉEL
/// du disque (il est donc idempotent) et ne supprime la copie que lorsque l'original est revenu à sa place.
/// </summary>
internal static class MoveProcedures
{
    public static RollbackReport RollbackMove(IFileSystem fs, OperationJournal? journal, MoveFacts facts, RetryPolicy retry)
    {
        TryAppend(journal, JournalSteps.RollbackStarted, "Retour à l'état initial.");

        // 1) Notre jonction à l'emplacement d'origine : la retirer (le lien seul).
        var atSource = fs.GetEntry(facts.Source);
        if (atSource is { Kind: FsEntryKind.Link })
        {
            if (!IsJunctionTo(fs, facts.Source, atSource.Identity, facts.Destination))
            {
                return Attention(journal, $"« {facts.Source} » est un lien qui ne pointe pas vers la copie : laissé en place.");
            }

            retry.Execute(() => fs.Delete(facts.Source, atSource.Identity));
            atSource = null;
        }
        else if (atSource is { Kind: FsEntryKind.Directory } && atSource.Identity != facts.SourceId)
        {
            // Un dossier ordinaire qui n'est pas l'original : jonction à moitié créée (dossier vide) ou dossier recréé par un tiers.
            var lockedNow = fs.GetEntry(facts.Locked);
            if (lockedNow is not null && lockedNow.Identity == facts.SourceId && IsEmptyDirectory(fs, facts.Source, atSource))
            {
                retry.Execute(() => fs.Delete(facts.Source, atSource.Identity));
                atSource = null;
            }
            else if (lockedNow is not null)
            {
                return Attention(journal, $"Un dossier a été recréé à « {facts.Source} » pendant l'opération : rien n'a été supprimé, l'original est intact sous « {facts.Locked} ».");
            }
        }

        // 2) Lever le verrou : remettre l'original à sa place.
        if (atSource is null)
        {
            var locked = fs.GetEntry(facts.Locked);
            if (locked is not null && facts.SourceId is { } sourceId && locked.Identity == sourceId)
            {
                retry.Execute(() => fs.Rename(facts.Locked, sourceId, PathUtil.GetName(facts.Source)));
            }
            else
            {
                return Attention(journal, $"L'original est introuvable (ni « {facts.Source} » ni « {facts.Locked} ») : la copie est conservée sous « {facts.Destination} ».");
            }
        }

        var restored = fs.GetEntry(facts.Source);
        var originalBack = restored is { Kind: FsEntryKind.Directory } && (facts.SourceId is null || restored.Identity == facts.SourceId);
        if (!originalBack)
        {
            return Attention(journal, $"L'original n'a pas pu être remis à « {facts.Source} » : la copie est conservée.");
        }

        // 3) L'original est revenu : la copie peut être supprimée (si c'est bien la nôtre).
        var destination = fs.GetEntry(facts.Destination);
        if (destination is not null)
        {
            if (facts.DestinationId is { } destinationId && destination.Identity == destinationId)
            {
                var deletion = new SafeDeleter(fs, retry).DeleteTree(facts.Destination, destinationId);
                if (!deletion.Complete)
                {
                    TryAppend(journal, JournalSteps.RolledBack, $"Original remis en place. La copie partielle n'a pas pu être entièrement supprimée : {deletion.Failures[0].Message}");
                    return new RollbackReport(true, $"Original remis en place ; copie partielle à supprimer manuellement : « {facts.Destination} ».");
                }
            }
            else if (facts.DestinationId is null && facts.DestinationIntent && destination.Kind == FsEntryKind.Directory && IsEmptyDirectory(fs, facts.Destination, destination))
            {
                retry.Execute(() => fs.Delete(facts.Destination, destination.Identity));
            }
            else
            {
                TryAppend(journal, JournalSteps.RolledBack, $"Original remis en place. « {facts.Destination} » n'a pas été créé par cette opération : laissé intact.");
                return new RollbackReport(true, "Original remis en place.");
            }
        }

        TryAppend(journal, JournalSteps.RolledBack, "Original remis en place, copie supprimée.");
        return new RollbackReport(true, "Tout est revenu à l'état initial.");
    }

    /// <summary>Après le point de non-retour : supprime l'original verrouillé, exactement d'après son manifeste.</summary>
    public static OperationOutcome FinishMove(IFileSystem fs, OperationJournal journal, MoveFacts facts, Manifest manifest, RetryPolicy retry, out string message)
    {
        try
        {
            var deletion = new SafeDeleter(fs, retry).DeleteEntries(facts.Locked, manifest.Entries.Select(e => (e.RelativePath, e.SourceIdentity)).ToList());
            if (deletion.Complete)
            {
                journal.Append(JournalSteps.CleanupDone, $"{deletion.Deleted} éléments de l'original supprimés.");
                journal.Append(JournalSteps.Completed, "Déplacement terminé.");
                message = "Déplacement terminé et vérifié.";
                return OperationOutcome.Completed;
            }

            message = $"Déplacement terminé et vérifié. L'ancien dossier « {facts.Locked} » n'a pas pu être entièrement supprimé ({deletion.Failures.Count} élément(s)) : nouvel essai au prochain lancement.";
            journal.Append(JournalSteps.CleanupPending, message, new Dictionary<string, string>
            {
                [JournalKeys.Failures] = string.Join(" | ", deletion.Failures.Take(5).Select(f => f.Message)),
            });
            return OperationOutcome.CompletedWithPendingCleanup;
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            message = $"Déplacement terminé et vérifié. Nettoyage de « {facts.Locked} » interrompu : {ex.Message}";
            TryAppend(journal, JournalSteps.CleanupPending, message);
            return OperationOutcome.CompletedWithPendingCleanup;
        }
    }

    public static bool IsJunctionTo(IFileSystem fs, string path, FileIdentity identity, string target)
    {
        var data = fs.ReadReparsePoint(path, identity);
        return data.IsJunction && data.Target is { } t
               && string.Equals(PathUtil.TrimTrailingSeparator(t), PathUtil.TrimTrailingSeparator(target), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEmptyDirectory(IFileSystem fs, string path, FsEntryInfo info)
        => info.Kind == FsEntryKind.Directory && fs.ListDirectory(path, info.Identity).Count == 0;

    public static void TryAppend(OperationJournal? journal, string step, string? message = null, IReadOnlyDictionary<string, string>? data = null)
    {
        try
        {
            journal?.Append(step, message, data);
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            // Journal indisponible pendant un rollback : l'état réel du disque fera foi à la reprise.
        }
    }

    private static RollbackReport Attention(OperationJournal? journal, string message)
    {
        TryAppend(journal, JournalSteps.NeedsAttention, message);
        return new RollbackReport(false, message);
    }
}
