using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;

namespace Demenageur.Core.Safety;

/// <summary>Simulation d'une annulation : ce qui serait fait, et si c'est possible.</summary>
public sealed class UndoPlan
{
    public required MovedSoftware Moved { get; init; }

    public required IReadOnlyList<RuleVerdict> Verdicts { get; init; }

    public required IReadOnlyList<(string Step, string Detail)> Steps { get; init; }

    public long CurrentBytes { get; init; }

    public long RequiredBytes { get; init; }

    public long FreeBytes { get; init; }

    public IReadOnlyList<string> ExecutableFiles { get; init; } = [];

    public bool CanExecute => Verdicts.All(v => v.Level != RuleLevel.Block);
}

/// <summary>Vérifications avant « Annuler le déplacement » (lecture seule).</summary>
public sealed class UndoPlanner
{
    private readonly IFileSystem _fs;
    private readonly IVolumeInspector _volumes;
    private readonly IProcessInspector _processes;

    public UndoPlanner(IFileSystem fs, IVolumeInspector volumes, IProcessInspector processes)
    {
        _fs = fs;
        _volumes = volumes;
        _processes = processes;
    }

    public UndoPlan Plan(MovedSoftware moved, CancellationToken cancellationToken = default)
    {
        var verdicts = new List<RuleVerdict>();
        void Block(string id, string title, string text) => verdicts.Add(new RuleVerdict(id, RuleLevel.Block, title, text));

        if (moved.Health is not (MoveHealth.Ok or MoveHealth.CleanupPending))
        {
            Block("U-SANTE", "État inattendu", moved.HealthMessage + " L'annulation automatique n'est pas possible : consulte l'aide (procédure manuelle).");
            return new UndoPlan { Moved = moved, Verdicts = verdicts, Steps = [] };
        }

        var size = new FolderSizeCalculator(_fs).Compute(moved.Destination, cancellationToken);
        long required = 0;
        long free = 0;
        try
        {
            var volume = _volumes.Describe(moved.Source);
            required = (long)Math.Ceiling((size.Bytes + (size.Files * 1024L)) * MovePlanner.SpaceMargin);
            free = volume.Volume.FreeBytesAvailable;
            if (required > free)
            {
                Block("U-ESPACE", "Espace insuffisant", $"Il faut {MovePlanner.Bytes(required)} sur {volume.Letter} pour y remettre le logiciel ; seulement {MovePlanner.Bytes(free)} disponibles.");
            }
        }
        catch (FileSystemException ex)
        {
            Block("U-VOLUME", "Volume inaccessible", ex.Message);
        }

        var snapshot = new TreeScanner(_fs).Scan(moved.Destination, new ScanOptions { IncludeStreams = false }, cancellationToken: cancellationToken);
        var files = snapshot.Entries
            .Where(e => e.Kind == FsEntryKind.File && (e.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || e.RelativePath.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)))
            .Select(e => PathUtil.JoinRelative(moved.Destination, e.RelativePath))
            .ToList();
        foreach (var process in _processes.FindUsers([moved.Destination, moved.Source], files))
        {
            Block("U-PROCESSUS", "Logiciel en cours d'utilisation", $"{process.Name} (PID {process.ProcessId}) {process.Reason}. Ferme-le avant l'annulation.");
        }

        return new UndoPlan
        {
            Moved = moved,
            Verdicts = verdicts,
            CurrentBytes = size.Bytes,
            RequiredBytes = required,
            FreeBytes = free,
            ExecutableFiles = files,
            Steps =
            [
                ("Verrouillage des fichiers déplacés", $"Renommer « {moved.Destination} » : le logiciel est hors ligne pendant la copie."),
                ("Copie de retour", $"Copier {MovePlanner.Bytes(size.Bytes)} vers « {moved.Source} » (dossier temporaire à côté)."),
                ("Vérification du contenu", "Relire toute la copie et comparer chaque fichier."),
                ("Permissions", "Reposer les permissions d'origine et les vérifier."),
                ("Remise en place", "Remplacer la jonction par le vrai dossier, puis vérifier. Point de non-retour."),
                ("Suppression de l'ancienne copie", $"Supprimer l'ancienne copie dans « {PathUtil.GetParent(moved.Destination)} »."),
            ],
        };
    }
}
