using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>
/// Historique effaçable : journaux dont plus rien ne dépend. Ne sont JAMAIS effacés les journaux d'un logiciel encore
/// déplacé (ils servent à l'annuler), ni ceux d'une opération inachevée, illisible ou n'appartenant pas aux administrateurs.
/// </summary>
public static class OperationHistory
{
    /// <summary>
    /// Opérations effaçables :
    /// <list type="bullet">
    /// <item>opérations abandonnées avec retour complet à l'état initial (aucun point de non-retour) ;</item>
    /// <item>déplacements annulés ou dont la copie a été nettoyée, avec l'opération d'annulation ou de nettoyage,
    /// une fois les deux entièrement terminées.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<StoredOperation> FindDeletable(IReadOnlyList<StoredOperation> operations)
    {
        static bool Settled(StoredOperation o) => o.IsReadable && o.IsTerminal;

        var byId = operations.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var deletable = new Dictionary<string, StoredOperation>(StringComparer.Ordinal);

        foreach (var operation in operations.Where(o => Settled(o) && !o.IsCommitted && o.LastStep == JournalSteps.RolledBack))
        {
            deletable[operation.Id] = operation;
        }

        foreach (var closing in operations.Where(o => o.Kind is OperationKind.Undo or OperationKind.Retire && Settled(o) && o.IsCompleted && o.IsCommitted))
        {
            var moveId = closing.Header?.Get(JournalKeys.MoveOperation);
            if (moveId is not null && byId.TryGetValue(moveId, out var move))
            {
                if (move.Kind != OperationKind.Move || !Settled(move) || !move.IsCompleted)
                {
                    continue; // déplacement encore en cours de nettoyage ou douteux : on garde les deux
                }

                deletable[move.Id] = move;
            }
            else if (moveId is null)
            {
                continue;
            }

            deletable[closing.Id] = closing;
        }

        return deletable.Values.OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Supprime le dossier d'une opération (identité vérifiée, sans suivre aucun lien). Renvoie false en cas d'échec partiel.</summary>
    public static bool Delete(IFileSystem guardedFs, StoredOperation operation)
    {
        if (guardedFs.GetEntry(operation.Directory) is not { Kind: FsEntryKind.Directory } entry)
        {
            return true;
        }

        return new SafeDeleter(guardedFs).DeleteTree(operation.Directory, entry.Identity).Complete;
    }
}
