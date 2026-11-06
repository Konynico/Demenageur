using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>État de santé d'un logiciel déplacé.</summary>
public enum MoveHealth
{
    /// <summary>Jonction en place vers la copie, copie présente.</summary>
    Ok,

    /// <summary>Déplacé ; l'ancien dossier n'a pas encore pu être entièrement supprimé.</summary>
    CleanupPending,

    /// <summary>La jonction a disparu ou pointe ailleurs.</summary>
    JunctionBroken,

    /// <summary>Un vrai dossier a remplacé la jonction (mise à jour ou réinstallation) : la copie est orpheline.</summary>
    ReplacedByFolder,

    /// <summary>La copie a disparu (disque débranché, supprimée, remplacée).</summary>
    DestinationMissing,

    /// <summary>La copie est vide (logiciel désinstallé ?).</summary>
    DestinationEmpty,

    /// <summary>Plus rien à l'emplacement d'origine (ni jonction ni dossier) : logiciel désinstallé ?</summary>
    SourceMissing,
}

/// <summary>Logiciel actuellement déplacé (déplacement validé, non annulé, non nettoyé).</summary>
public sealed record MovedSoftware(StoredOperation Move, string Source, string Destination, FileIdentity DestinationId, MoveHealth Health, string HealthMessage)
{
    public string SoftwareName => Move.SoftwareName;

    /// <summary>La jonction mène toujours à la copie.</summary>
    public bool JunctionActive => Health is MoveHealth.Ok or MoveHealth.CleanupPending or MoveHealth.DestinationEmpty;

    /// <summary>
    /// Le déplacement est devenu sans objet et sa copie peut être nettoyée sans toucher au logiciel :
    /// plus de jonction (désinstallé), ou un vrai dossier l'a remplacée (mise à jour, réinstallation).
    /// </summary>
    public bool IsOrphaned => Health is MoveHealth.SourceMissing or MoveHealth.ReplacedByFolder or MoveHealth.JunctionBroken or MoveHealth.DestinationEmpty;

    public RetireRequest ToRetireRequest(bool removeActiveJunction) => new()
    {
        MoveOperationId = Move.Id,
        SoftwareName = SoftwareName,
        SourcePath = Source,
        DestinationPath = Destination,
        DestinationIdentity = DestinationId,
        RemoveActiveJunction = removeActiveJunction,
    };
}

/// <summary>
/// Liste des déplacements en vigueur, d'après les journaux, avec un bilan de santé lu sur le disque.
/// Ne fait JAMAIS rien d'autre que lire.
/// </summary>
public static class MovedSoftwareRegistry
{
    public static IReadOnlyList<MovedSoftware> Load(OperationStore store, IFileSystem fs)
    {
        var operations = store.List();
        // Annulés, ou nettoyés (le point de non-retour du nettoyage suffit : la reprise terminera la suppression).
        var undone = operations
            .Where(o => o.Kind is OperationKind.Undo or OperationKind.Retire && o.IsReadable && o.IsCommitted)
            .Select(o => o.Header?.Get(JournalKeys.MoveOperation))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<MovedSoftware>();
        foreach (var move in operations.Where(o => o.Kind == OperationKind.Move && o.IsReadable && o.IsCommitted && !undone.Contains(o.Id)))
        {
            if (move.IdentityFrom(JournalSteps.DestinationCreated, JournalKeys.DestinationId) is not { } destinationId)
            {
                continue;
            }

            var (health, message) = Diagnose(fs, move, destinationId);
            result.Add(new MovedSoftware(move, move.SourcePath!, move.DestinationPath!, destinationId, health, message));
        }

        // Un même dossier déplacé plusieurs fois (déplacé, annulé, redéplacé) : seul le plus récent compte.
        return result
            .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(m => m.Move.Id, StringComparer.Ordinal).First())
            .OrderBy(m => m.SoftwareName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static (MoveHealth, string) Diagnose(IFileSystem fs, StoredOperation move, FileIdentity destinationId)
    {
        try
        {
            var destination = fs.GetEntry(move.DestinationPath!);
            var source = fs.GetEntry(move.SourcePath!);
            if (source is { Kind: FsEntryKind.Directory })
            {
                return (MoveHealth.ReplacedByFolder, "Un vrai dossier a remplacé la jonction (mise à jour ou réinstallation ?) : le logiciel tourne de nouveau depuis son emplacement d'origine et la copie sur le disque cible est orpheline. Tu peux la nettoyer, puis redéplacer le logiciel.");
            }

            if (source is null)
            {
                return destination is null || destination.Identity != destinationId
                    ? (MoveHealth.SourceMissing, "Le logiciel a été désinstallé : ni la jonction ni la copie n'existent plus. Il suffit de retirer ce déplacement de la liste.")
                    : (MoveHealth.SourceMissing, "La jonction a disparu : le logiciel a probablement été désinstallé. Ce que le désinstalleur a laissé est resté dans la copie sur le disque cible ; tu peux la nettoyer.");
            }

            if (source is not { Kind: FsEntryKind.Link } || !MoveProcedures.IsJunctionTo(fs, move.SourcePath!, source.Identity, move.DestinationPath!))
            {
                return (MoveHealth.JunctionBroken, "La jonction à l'emplacement d'origine a disparu ou pointe ailleurs.");
            }

            if (destination is null || destination.Identity != destinationId)
            {
                return (MoveHealth.DestinationMissing, "La copie est introuvable (disque débranché ou dossier supprimé) : le logiciel ne peut pas fonctionner.");
            }

            if (fs.ListDirectory(move.DestinationPath!, destinationId).Count == 0)
            {
                return (MoveHealth.DestinationEmpty, "La copie est vide : le logiciel a probablement été désinstallé.");
            }

            return move.IsCompleted
                ? (MoveHealth.Ok, "Jonction en place, copie présente.")
                : (MoveHealth.CleanupPending, "Déplacé et vérifié ; l'ancien dossier sera supprimé au prochain lancement.");
        }
        catch (FileSystemException ex)
        {
            return (MoveHealth.DestinationMissing, "Vérification impossible : " + ex.Message);
        }
    }
}
