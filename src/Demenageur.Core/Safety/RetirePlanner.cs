using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;

namespace Demenageur.Core.Safety;

/// <summary>Simulation d'un nettoyage : ce qui serait supprimé, et si c'est permis.</summary>
public sealed class RetirePlan
{
    public required MovedSoftware Moved { get; init; }

    public required IReadOnlyList<RuleVerdict> Verdicts { get; init; }

    public required IReadOnlyList<(string Step, string Detail)> Steps { get; init; }

    /// <summary>Taille et nombre de fichiers de la copie qui serait supprimée.</summary>
    public long CopyBytes { get; init; }

    public int CopyFiles { get; init; }

    /// <summary>La jonction mène encore à la copie : elle serait supprimée aussi.</summary>
    public bool RemoveActiveJunction { get; init; }

    /// <summary>Saisie du nom exigée (la jonction encore active sera supprimée).</summary>
    public bool RequiresStrongConfirmation { get; init; }

    public bool CanExecute => Verdicts.All(v => v.Level != RuleLevel.Block);
}

/// <summary>
/// Vérifications avant de nettoyer un déplacement devenu sans objet (lecture seule).
/// Le cas sûr : plus de jonction (désinstallé) ou un vrai dossier à sa place (mise à jour) ; seule l'ancienne copie
/// est supprimée. Si la jonction mène encore à la copie, le nettoyage n'est permis que si plus aucune entrée de
/// désinstallation ne désigne le dossier, et avec saisie du nom.
/// </summary>
public sealed class RetirePlanner
{
    private readonly IFileSystem _fs;
    private readonly IProcessInspector _processes;

    public RetirePlanner(IFileSystem fs, IProcessInspector processes)
    {
        _fs = fs;
        _processes = processes;
    }

    public RetirePlan Plan(MovedSoftware moved, IReadOnlyList<UninstallEntry> registrations, CancellationToken cancellationToken = default)
    {
        var verdicts = new List<RuleVerdict>();
        void Add(RuleLevel level, string id, string title, string text) => verdicts.Add(new RuleVerdict(id, level, title, text));

        var junctionActive = moved.JunctionActive;
        switch (moved.Health)
        {
            case MoveHealth.CleanupPending:
                Add(RuleLevel.Block, "N-EN-COURS", "Déplacement pas encore terminé", "L'ancien dossier d'origine attend encore d'être supprimé : relance Déménageur pour terminer ce déplacement d'abord.");
                break;
            case MoveHealth.DestinationMissing:
                Add(RuleLevel.Block, "N-INTROUVABLE", "Copie introuvable", "La jonction est en place mais la copie est introuvable (disque débranché, verrouillé ou lettre changée ?). Rien n'est nettoyé : rebranche le disque.");
                break;
            case MoveHealth.SourceMissing:
                Add(RuleLevel.Info, "N-DESINSTALLE", "Logiciel désinstallé", $"Plus rien à l'emplacement d'origine « {moved.Source} » : le désinstalleur a supprimé la jonction. Il n'y a rien à y toucher.");
                break;
            case MoveHealth.ReplacedByFolder:
                Add(RuleLevel.Info, "N-REMPLACE", "Remplacé par un vrai dossier", $"Une mise à jour ou une réinstallation a recréé « {moved.Source} » : le logiciel fonctionne depuis ce dossier, qui ne sera PAS touché. Seule l'ancienne copie est supprimée ; tu pourras ensuite redéplacer le logiciel.");
                break;
            case MoveHealth.JunctionBroken:
                Add(RuleLevel.Info, "N-AILLEURS", "Jonction modifiée", $"« {moved.Source} » est un lien qui ne mène plus à la copie : il ne sera pas touché.");
                break;
        }

        if (junctionActive && moved.Health is MoveHealth.Ok or MoveHealth.DestinationEmpty)
        {
            if (registrations.Count > 0)
            {
                Add(RuleLevel.Block, "N-INSTALLE", "Logiciel toujours installé",
                    $"Windows le liste encore comme installé ({string.Join(", ", registrations.Select(r => r.DisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase))}). "
                    + "Désinstalle-le normalement (son désinstalleur supprime ses fichiers à travers la jonction), puis reviens nettoyer ce qui reste ; "
                    + "ou utilise « Annuler le déplacement » pour le remettre à sa place.");
            }
            else
            {
                Add(RuleLevel.Warning, "N-JONCTION", "La jonction sera supprimée",
                    $"« {moved.Source} » mène encore à la copie, mais plus aucun programme installé n'y fait référence : le logiciel semble désinstallé, son désinstalleur ayant laissé la jonction. "
                    + "La jonction (le lien seul) et la copie seront supprimées. Si le logiciel était encore utilisé, il ne fonctionnerait plus.");
            }
        }

        long bytes = 0;
        var files = 0;
        var executables = new List<string>();
        var copy = _fs.GetEntry(moved.Destination);
        if (copy is null)
        {
            Add(RuleLevel.Info, "N-RIEN", "Copie déjà supprimée", "La copie n'existe plus : le déplacement sera simplement retiré de la liste.");
        }
        else if (copy.Kind != FsEntryKind.Directory || copy.Identity != moved.DestinationId)
        {
            Add(RuleLevel.Block, "N-IDENTITE", "Dossier inconnu", $"« {moved.Destination} » n'est plus le dossier créé par le déplacement : Déménageur refuse de le supprimer.");
        }
        else
        {
            var snapshot = new TreeScanner(_fs).Scan(moved.Destination, new ScanOptions { IncludeStreams = false }, cancellationToken: cancellationToken);
            foreach (var entry in snapshot.Entries.Where(e => e.Kind == FsEntryKind.File))
            {
                bytes += entry.Info.Size;
                files++;
                if (entry.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entry.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    executables.Add(PathUtil.JoinRelative(moved.Destination, entry.RelativePath));
                }
            }

            if (files > 0)
            {
                Add(RuleLevel.Warning, "N-DEFINITIF", "Suppression définitive",
                    $"{files} fichier(s), {MovePlanner.Bytes(bytes)}, seront supprimés sans passer par la corbeille. "
                    + "Ouvre la copie pour vérifier qu'elle ne contient rien à garder (sauvegardes, configurations…).");
            }

            foreach (var process in _processes.FindUsers([moved.Destination], executables))
            {
                Add(RuleLevel.Block, "N-PROCESSUS", "Copie en cours d'utilisation", $"{process.Name} (PID {process.ProcessId}) {process.Reason}. Ferme-le avant le nettoyage.");
            }
        }

        var steps = new List<(string, string)>
        {
            (RetireSteps.Checks, "Contrôler que la copie est bien celle créée par le déplacement et que rien ne l'utilise."),
        };
        if (copy is not null)
        {
            steps.Add((RetireSteps.Lock, $"Renommer « {moved.Destination} » (échoue si un fichier est ouvert)."));
        }

        if (junctionActive)
        {
            steps.Add((RetireSteps.RemoveJunction, $"Supprimer la jonction « {moved.Source} » (le lien seul). Point de non-retour."));
        }

        if (copy is not null)
        {
            steps.Add((RetireSteps.Delete, $"Supprimer {files} fichier(s), {MovePlanner.Bytes(bytes)}, élément par élément, sans jamais suivre un lien."));
        }

        return new RetirePlan
        {
            Moved = moved,
            Verdicts = verdicts,
            Steps = steps,
            CopyBytes = bytes,
            CopyFiles = files,
            RemoveActiveJunction = junctionActive,
            RequiresStrongConfirmation = junctionActive && files > 0,
        };
    }
}
