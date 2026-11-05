using System.Globalization;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;

namespace Demenageur.Core.Safety;

/// <summary>Demande de simulation d'un déplacement.</summary>
public sealed record MovePlanRequest(MoveCandidate Candidate, string DestinationRoot)
{
    /// <summary>L'utilisateur accepte d'arrêter l'Explorateur pendant le verrou (extension shell).</summary>
    public bool StopExplorer { get; init; }

    /// <summary>L'utilisateur accepte d'arrêter les services concernés pendant le déplacement.</summary>
    public bool StopServices { get; init; }
}

/// <summary>Résultat de la simulation : ce qui serait fait, et pourquoi c'est (ou non) possible.</summary>
public sealed class MovePlan
{
    public required MovePlanRequest Request { get; init; }

    public required string Source { get; init; }

    public required string Destination { get; init; }

    public required IReadOnlyList<RuleVerdict> Verdicts { get; init; }

    public TreeSnapshot? Snapshot { get; init; }

    public long LogicalBytes { get; init; }

    public long RequiredBytes { get; init; }

    public long FreeBytes { get; init; }

    public VolumeDetails? SourceVolume { get; init; }

    public VolumeDetails? DestinationVolume { get; init; }

    public IReadOnlyList<ProcessUsage> Processes { get; init; } = [];

    /// <summary>Étapes, avec le détail de ce qui serait fait.</summary>
    public required IReadOnlyList<(string Step, string Detail)> Steps { get; init; }

    public TimeSpan EstimatedDuration { get; init; }

    public bool CanExecute => Verdicts.All(v => v.Level != RuleLevel.Block);

    public bool RequiresStrongConfirmation => Verdicts.Any(v => v.Level == RuleLevel.Confirm);

    /// <summary>Le seul obstacle est l'Explorateur (extension shell) : l'option « Redémarrer l'Explorateur » le lèverait.</summary>
    public bool ExplorerIsOnlyBlocker => Processes.Count > 0 && Processes.All(p => p.IsExplorer)
        && Verdicts.Where(v => v.Level == RuleLevel.Block).All(v => v.RuleId == "R-EXPLORATEUR");

    /// <summary>Les seuls obstacles sont des services en cours d'exécution : l'option « arrêter les services » les lèverait.</summary>
    public IReadOnlyList<string> RunningServices => Processes.Where(p => p.ServiceName is not null).Select(p => p.ServiceName!).Distinct().ToList();
}

/// <summary>
/// Simulation d'un déplacement (dry-run), en LECTURE SEULE : rien n'est créé ni modifié.
/// Vérifie la source, la liste noire, la destination, les volumes, l'espace, les processus,
/// inventorie toute l'arborescence (et tente d'ouvrir chaque fichier en lecture), puis liste les étapes.
/// </summary>
public sealed class MovePlanner
{
    public const double SpaceMargin = 1.10;
    public const long JournalReserveBytes = 200L * 1024 * 1024;

    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".sys", ".ocx", ".cpl", ".ax", ".node", ".pyd", ".scr", ".drv", ".mui"];

    private readonly IFileSystem _fs;
    private readonly IKnownPaths _known;
    private readonly IVolumeInspector _volumes;
    private readonly IProcessInspector _processes;
    private readonly Func<SystemSnapshot> _system;
    private readonly OperationStore _store;

    public MovePlanner(IFileSystem fs, IKnownPaths known, IVolumeInspector volumes, IProcessInspector processes, Func<SystemSnapshot> system, OperationStore store)
    {
        _fs = fs;
        _known = known;
        _volumes = volumes;
        _processes = processes;
        _system = system;
        _store = store;
    }

    public MovePlan Plan(MovePlanRequest request, IReadOnlyList<InstalledSoftware> catalog, CancellationToken cancellationToken = default)
    {
        var verdicts = new List<RuleVerdict>();
        void Block(string id, string title, string explanation) => verdicts.Add(new RuleVerdict(id, RuleLevel.Block, title, explanation));

        if (!PathUtil.TryValidateLocalPath(request.Candidate.Folder, out var source, out var sourceError))
        {
            Block("R-SOURCE", "Dossier invalide", $"« {request.Candidate.Folder} » : {sourceError}.");
            return Empty(request, request.Candidate.Folder, string.Empty, verdicts);
        }

        if (!PathUtil.TryValidateLocalPath(request.DestinationRoot, out var destinationRoot, out var destinationError))
        {
            Block("R-DEST", "Destination invalide", $"« {request.DestinationRoot} » : {destinationError}.");
            return Empty(request, source, string.Empty, verdicts);
        }

        var destination = PathUtil.Join(destinationRoot, PathUtil.GetName(source));

        // ── Source ──
        var sourceEntry = _fs.GetEntry(source);
        if (sourceEntry is null)
        {
            Block("R-SOURCE", "Dossier introuvable", $"« {source} » n'existe pas.");
            return Empty(request, source, destination, verdicts);
        }

        if (sourceEntry.Kind == FsEntryKind.Link)
        {
            Block("R-DEJA", "Déjà déplacé", $"« {source} » est déjà une jonction (déplacé par Déménageur ou un autre outil).");
            return Empty(request, source, destination, verdicts);
        }

        if (sourceEntry.Kind != FsEntryKind.Directory)
        {
            Block("R-SOURCE", "Pas un dossier", $"« {source} » n'est pas un dossier ordinaire.");
            return Empty(request, source, destination, verdicts);
        }

        for (var ancestor = PathUtil.GetParent(source); ancestor is not null; ancestor = PathUtil.GetParent(ancestor))
        {
            FsEntryInfo? ancestorEntry;
            try
            {
                ancestorEntry = _fs.GetEntry(ancestor);
            }
            catch (PathGuardViolationException)
            {
                break; // hors de la zone lisible (bac à sable de test)
            }

            if (ancestorEntry is { Kind: FsEntryKind.Link or FsEntryKind.UnsupportedReparsePoint })
            {
                Block("R-PARENT-LIEN", "Dossier parent déjà redirigé", $"« {ancestor} » est une jonction ou un lien : déplace plutôt depuis l'emplacement réel.");
            }
        }

        foreach (var moved in MovedSoftwareRegistry.Load(_store, _fs))
        {
            if (PathUtil.IsWithinIgnoreCase(source, moved.Destination))
            {
                Block("R-IMBRIQUE", "Dans un logiciel déplacé", $"Ce dossier se trouve dans la copie de « {moved.SoftwareName} » déjà déplacé.");
            }

            if (PathUtil.IsWithinIgnoreCase(moved.Source, source))
            {
                Block("R-IMBRIQUE", "Contient un logiciel déplacé", $"Ce dossier contient « {moved.SoftwareName} », déjà déplacé : annule d'abord ce déplacement.");
            }
        }

        // ── Liste noire ──
        verdicts.AddRange(new SafetyRules(_known, _fs).Evaluate(request.Candidate, _system(), catalog));

        // ── Destination ──
        if (PathUtil.IsWithinIgnoreCase(destinationRoot, source) || PathUtil.IsWithinIgnoreCase(source, destinationRoot))
        {
            Block("R-DEST-IMBRIQUEE", "Destination imbriquée", "La destination ne peut être ni dans le dossier à déplacer, ni le contenir.");
        }

        if (_fs.GetEntry(destination) is not null)
        {
            Block("R-DEST-EXISTE", "Destination déjà occupée", $"« {destination} » existe déjà : choisis un autre dossier de destination.");
        }

        if (DestinationContainer.Check(_fs, destinationRoot) is { } containerVerdict)
        {
            verdicts.Add(containerVerdict);
        }

        // ── Volumes ──
        VolumeDetails? sourceVolume = null;
        VolumeDetails? destinationVolume = null;
        try
        {
            sourceVolume = _volumes.Describe(source);
            destinationVolume = _volumes.Describe(destinationRoot);
        }
        catch (FileSystemException ex)
        {
            Block("R-VOLUME", "Volume inaccessible", ex.Message);
        }

        if (sourceVolume is not null && destinationVolume is not null)
        {
            CheckVolumes(sourceVolume, destinationVolume, verdicts);
        }

        // ── Inventaire complet (lecture seule) ──
        var snapshot = new TreeScanner(_fs).Scan(source, ScanOptions.Full, cancellationToken: cancellationToken);
        foreach (var problem in snapshot.Problems)
        {
            verdicts.Add(new RuleVerdict("R-INVENTAIRE",
                problem.Severity == ScanProblemSeverity.Blocking ? RuleLevel.Block : (problem.Code is ScanProblemCode.LinkInside or ScanProblemCode.HardLinkInside ? RuleLevel.Info : RuleLevel.Warning),
                problem.Severity == ScanProblemSeverity.Blocking ? "Contenu non pris en charge" : "À savoir", problem.Message));
        }

        var unreadable = new List<string>();
        foreach (var entry in snapshot.Entries.Where(e => e.Kind == FsEntryKind.File))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _fs.ProbeRead(PathUtil.JoinRelative(source, entry.RelativePath), entry.Info.Identity);
            }
            catch (FileSystemException ex)
            {
                unreadable.Add($"{entry.RelativePath} ({ex.Kind})");
            }
        }

        if (unreadable.Count > 0)
        {
            Block("R-LECTURE", "Fichiers illisibles", $"{unreadable.Count} fichier(s) ne peuvent pas être lus : " + string.Join(", ", unreadable.Take(5)) + (unreadable.Count > 5 ? "…" : string.Empty));
        }

        // ── Espace ──
        long required = 0;
        long free = 0;
        if (destinationVolume is not null)
        {
            required = (long)Math.Ceiling(snapshot.EstimateAllocatedBytes(destinationVolume.Volume.ClusterSize) * SpaceMargin);
            free = destinationVolume.Volume.FreeBytesAvailable;
            if (required > free)
            {
                Block("R-ESPACE", "Espace insuffisant",
                    $"Il faut {Bytes(required)} (taille + 10 %) sur {destinationVolume.Letter}, seulement {Bytes(free)} disponibles.");
            }
        }

        try
        {
            var dataVolume = _fs.GetVolumeInfo(_store.Root);
            if (dataVolume.FreeBytesAvailable < JournalReserveBytes)
            {
                Block("R-ESPACE-JOURNAL", "Espace insuffisant pour le journal", $"Il faut au moins {Bytes(JournalReserveBytes)} libres sur {dataVolume.RootPath} pour le journal des opérations.");
            }
        }
        catch (FileSystemException)
        {
            // Volume des données indisponible : signalé à l'exécution.
        }

        // ── Processus ──
        var files = snapshot.Entries
            .Where(e => e.Kind == FsEntryKind.File && ExecutableExtensions.Any(x => e.RelativePath.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Select(e => PathUtil.JoinRelative(source, e.RelativePath))
            .ToList();
        var processes = _processes.FindUsers([source], files);
        foreach (var process in processes)
        {
            if (process.IsExplorer && request.StopExplorer)
            {
                verdicts.Add(new RuleVerdict("R-EXPLORATEUR", RuleLevel.Info, "Explorateur", "L'Explorateur sera arrêté pendant le verrouillage, puis relancé."));
            }
            else if (process.ServiceName is not null && request.StopServices)
            {
                verdicts.Add(new RuleVerdict("R-SERVICE-ARRET", RuleLevel.Confirm, "Service arrêté pendant le déplacement", $"Le service « {process.ServiceName} » sera arrêté, puis redémarré."));
            }
            else
            {
                Block(process.IsExplorer ? "R-EXPLORATEUR" : "R-PROCESSUS",
                    process.IsExplorer ? "L'Explorateur utilise ce logiciel" : "Logiciel en cours d'utilisation",
                    $"{process.Name} (PID {process.ProcessId}) {process.Reason}. Ferme-le avant le déplacement.");
            }
        }

        var bytes = snapshot.TotalBytes;
        var steps = BuildSteps(source, destination, snapshot, required);
        var duration = TimeSpan.FromSeconds(3.0 * bytes / (destinationVolume?.BusType == DiskBusType.Internal ? 400e6 : 120e6) + snapshot.Entries.Count * 0.004 + 2);

        return new MovePlan
        {
            Request = request,
            Source = source,
            Destination = destination,
            Verdicts = verdicts,
            Snapshot = snapshot,
            LogicalBytes = bytes,
            RequiredBytes = required,
            FreeBytes = free,
            SourceVolume = sourceVolume,
            DestinationVolume = destinationVolume,
            Processes = processes,
            Steps = steps,
            EstimatedDuration = duration,
        };
    }

    private static void CheckVolumes(VolumeDetails source, VolumeDetails destination, List<RuleVerdict> verdicts)
    {
        void Block(string title, string explanation) => verdicts.Add(new RuleVerdict("R-VOLUME", RuleLevel.Block, title, explanation));

        if (!source.Volume.IsNtfs)
        {
            Block("Volume source non NTFS", $"{source.Letter} est en {source.Volume.FileSystemName} : une jonction ne peut y être créée.");
        }

        if (!destination.Volume.IsNtfs)
        {
            Block("Destination non NTFS", $"{destination.Letter} est en {destination.Volume.FileSystemName} : seul NTFS conserve permissions, flux et attributs (et garantit une jonction fiable).");
        }

        if (source.Volume.VolumeGuidPath is not null && string.Equals(source.Volume.VolumeGuidPath, destination.Volume.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
        {
            Block("Même volume", "La destination est sur le même volume que le logiciel : le déplacer ne libérerait aucune place.");
        }

        if (destination.Volume.DriveType != VolumeDriveType.Fixed || destination.BusType is DiskBusType.Usb or DiskBusType.Removable or DiskBusType.Network or DiskBusType.Virtual)
        {
            Block("Disque cible non interne",
                $"{destination.Letter} est un disque {destination.BusDescription} : s'il est débranché ou monté plus tard, la jonction ne mène plus nulle part et le logiciel (et ses services) ne démarrent plus.");
        }

        if (destination.IsSubst)
        {
            Block("Lecteur virtuel (subst)", $"{destination.Letter} est un lecteur créé par « subst » : il disparaît à la fermeture de session.");
        }

        if (destination.BitLockerRisk == true)
        {
            Block("Disque chiffré sans déverrouillage automatique", $"{destination.Letter} est chiffré par BitLocker et n'est pas déverrouillé automatiquement au démarrage.");
        }
    }

    private static IReadOnlyList<(string, string)> BuildSteps(string source, string destination, TreeSnapshot snapshot, long required)
        =>
        [
            (MoveSteps.CreateDestination, $"Créer « {destination} » (accès réservé pendant l'opération)."),
            (MoveSteps.CheckProcesses, "Vérifier une dernière fois qu'aucun processus n'utilise le logiciel."),
            (MoveSteps.Lock, $"Renommer « {source} » en « {PathUtil.GetName(source)}.dmv-old-… » : le logiciel est hors ligne pendant la copie."),
            (MoveSteps.Inventory, $"Inventorier {snapshot.FileCount.ToString("N0", CultureInfo.CurrentCulture)} fichiers et {snapshot.DirectoryCount.ToString("N0", CultureInfo.CurrentCulture)} dossiers."),
            (MoveSteps.Copy, $"Copier {Bytes(snapshot.TotalBytes)} (espace réservé : {Bytes(required)})."),
            (MoveSteps.VerifyContent, "Relire toute la copie sans cache et comparer chaque fichier (XxHash128), les dates, attributs et flux."),
            (MoveSteps.Security, "Poser les permissions d'origine et les vérifier."),
            (MoveSteps.CreateJunction, $"Créer la jonction « {source} » → « {destination} » (comme mklink /J)."),
            (MoveSteps.ValidateJunction, "Vérifier le contenu vu à travers la jonction. Point de non-retour."),
            (MoveSteps.DeleteOriginal, "Supprimer l'ancien dossier, fichier par fichier, d'après l'inventaire."),
        ];

    public static string Bytes(long value)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static MovePlan Empty(MovePlanRequest request, string source, string destination, List<RuleVerdict> verdicts)
        => new() { Request = request, Source = source, Destination = destination, Verdicts = verdicts, Steps = [] };
}
