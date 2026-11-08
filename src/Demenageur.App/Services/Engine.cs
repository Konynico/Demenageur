using System.IO;
using System.Security.Principal;
using Demenageur.Core.Abstractions;
using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Windows.AppData;
using Demenageur.Windows.FileSystem;
using Demenageur.Windows.Inventory;
using Demenageur.Windows.Safety;
using Demenageur.Windows.SystemServices;
using Serilog;

namespace Demenageur.App.Services;

/// <summary>
/// Point d'entrée unique de l'interface vers le moteur : inventaire, simulation, déplacement, annulation, reprise.
/// Toutes les opérations longues sont à appeler hors du fil de l'interface.
/// </summary>
public sealed class Engine
{
    private readonly object _systemLock = new();
    private SystemSnapshot? _system;

    public Engine(IAppDataLocation data, bool preview)
    {
        Data = data;
        IsPreview = preview;
        Raw = new Win32FileSystem();
        Known = new WindowsKnownPaths(AppContext.BaseDirectory, data.DataRoot);
        using var identity = WindowsIdentity.GetCurrent();
        Store = new OperationStore(Raw, data.OperationsDirectory,
            preview ? [.. OperationStore.DefaultTrustedOwners(), identity.User!] : OperationStore.DefaultTrustedOwners());
        ScopeFactory = BuildScopeFactory(Known);
        Processes = new RestartManagerProcessInspector();
        Volumes = new WindowsVolumeInspector(Raw);
        RestorePoints = new RestorePointService();
        Power = new WindowsPowerStatus();
        SettingsStore = new SettingsStore(data.DataRoot);
        Settings = SettingsStore.Load();
        if (preview)
        {
            Settings.SimulationOnly = true;
        }

        Catalog = new SoftwareCatalog(new RegistryUninstallSource(), new InstallFolderResolver(Raw, Known));
    }

    public IAppDataLocation Data { get; }

    /// <summary>Mode aperçu (développement de l'interface) : sans droits, simulation uniquement.</summary>
    public bool IsPreview { get; }

    public IFileSystem Raw { get; }

    public IKnownPaths Known { get; }

    public OperationStore Store { get; }

    public IPathScopeFactory ScopeFactory { get; }

    public IProcessInspector Processes { get; }

    public IVolumeInspector Volumes { get; }

    public IRestorePointService RestorePoints { get; }

    public IPowerStatus Power { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public SoftwareCatalog Catalog { get; }

    /// <summary>Une opération réelle est en cours (fermeture de l'app et arrêt de Windows bloqués).</summary>
    public bool OperationInProgress { get; set; }

    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>
    /// État système (services, sécurité, COM, lanceurs…), lu une fois puis gardé en cache ;
    /// les exclusions de l'utilisateur, elles, sont toujours celles des réglages actuels.
    /// </summary>
    public SystemSnapshot System
    {
        get
        {
            lock (_systemLock)
            {
                _system ??= new WindowsSystemInspector().Capture();
                return _system with { UserExclusions = Settings.Exclusions.ToList() };
            }
        }
    }

    /// <summary>Ajoute un dossier aux exclusions (sans doublon) et enregistre les réglages.</summary>
    public void AddExclusion(string path, string? note)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path.Trim());
        if (!Settings.Exclusions.Any(x => string.Equals(x.Path, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.Exclusions.Add(new UserExclusion(normalized, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            SaveIfReal();
        }
    }

    public void RemoveExclusion(UserExclusion exclusion)
    {
        Settings.Exclusions.RemoveAll(x => string.Equals(x.Path, exclusion.Path, StringComparison.OrdinalIgnoreCase));
        SaveIfReal();
    }

    /// <summary>La liste des exclusions a changé (pour rafraîchir les pages qui l'affichent).</summary>
    public event Action? ExclusionsChanged;

    private int _changeCount;

    /// <summary>
    /// Compteur incrémenté à chaque opération (déplacement, annulation, nettoyage, reprise) et à chaque changement
    /// des exclusions : une page compare sa valeur à celle de son dernier chargement pour savoir si elle est périmée.
    /// </summary>
    public int ChangeCount => Volatile.Read(ref _changeCount);

    private void MarkChanged() => Interlocked.Increment(ref _changeCount);

    private void SaveIfReal()
    {
        MarkChanged();
        ExclusionsChanged?.Invoke();
        if (!IsPreview)
        {
            SaveSettings();
        }
    }

    public void RefreshSystem()
    {
        lock (_systemLock)
        {
            _system = null;
        }
    }

    /// <summary>Périmètre de production : racines et dossiers système jamais modifiables.</summary>
    private static PathScopeFactory BuildScopeFactory(IKnownPaths known)
    {
        var exact = new List<string>
        {
            known.ProgramFiles, known.ProgramFilesX86, known.ProgramData, known.UsersDirectory,
            known.CommonProgramFiles, known.CommonProgramFilesX86, known.ApplicationDataDirectory,
        };
        foreach (var profile in known.UserProfiles)
        {
            exact.Add(profile);
            exact.AddRange(new[] { "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Programs" }.Select(s => Path.Combine(profile, s)));
        }

        exact.AddRange(known.PersonalFolders);
        var subtrees = new List<string>
        {
            known.WindowsDirectory,
            Path.Combine(known.ProgramFiles, "WindowsApps"),
            Path.Combine(known.ProgramData, "Microsoft", "Windows"),
            known.ApplicationDirectory,
        };
        return new PathScopeFactory(exact, subtrees);
    }

    private MoveEngineContext Context(IMoveHooks hooks) => new()
    {
        FileSystem = Raw,
        ScopeFactory = ScopeFactory,
        Store = Store,
        Hooks = hooks,
        Retry = RetryPolicy.Default,
    };

    public SafetyRules CreateRules() => new(Known, Raw);

    public MovePlanner CreatePlanner() => new(Raw, Known, Volumes, Processes, () => System, Store);

    public IReadOnlyList<MovedSoftware> LoadMoves() => MovedSoftwareRegistry.Load(Store, Raw);

    public OperationResult ExecuteMove(MovePlan plan, Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        EnsureRealOperationsAllowed();
        Log.Information("Déplacement de {Software} : {Source} → {Destination}", plan.Request.Candidate.DisplayName, plan.Source, plan.Destination);
        DestinationContainer.EnsureCreated(Raw, ScopeFactory, plan.Request.DestinationRoot);
        var hooks = new WindowsMoveHooks(new CriticalSectionOptions
        {
            StopExplorer = plan.Request.StopExplorer,
            ServicesToStop = plan.Request.StopServices ? plan.RunningServices : [],
            ExecutableFiles = plan.ExecutableFiles,
        }, Processes, Data.DataRoot);
        var result = new MoveTransaction(Context(hooks)).Execute(new MoveRequest(plan.Request.Candidate.DisplayName, plan.Source, plan.Destination), progress, cancellationToken);
        LogResult(result);
        UpdateReadme(plan.Request.DestinationRoot);
        return result;
    }

    public UndoPlan PlanUndo(MovedSoftware moved) => new UndoPlanner(Raw, Volumes, Processes).Plan(moved);

    public OperationResult ExecuteUndo(UndoPlan plan, Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        EnsureRealOperationsAllowed();
        Log.Information("Annulation du déplacement de {Software}", plan.Moved.SoftwareName);
        var request = UndoRequestFactory.FromMove(Store, plan.Moved.Move);
        var hooks = new WindowsMoveHooks(new CriticalSectionOptions { ExecutableFiles = plan.ExecutableFiles }, Processes, Data.DataRoot);
        var result = new UndoTransaction(Context(hooks)).Execute(request, progress, cancellationToken);
        LogResult(result);
        UpdateReadme(Path.GetDirectoryName(plan.Moved.Destination)!);
        return result;
    }

    /// <summary>Entrées de désinstallation qui désignent encore l'emplacement d'origine (logiciel toujours installé).</summary>
    public IReadOnlyList<UninstallEntry> RegistrationsOf(MovedSoftware moved)
        => UninstallReferences.PointingInto(Catalog.ReadEntries(), moved.Source);

    /// <summary>Simulation du nettoyage d'un déplacement devenu sans objet (lecture seule).</summary>
    public RetirePlan PlanRetire(MovedSoftware moved) => new RetirePlanner(Raw, Processes).Plan(moved, RegistrationsOf(moved));

    /// <summary>Nettoyage d'un déplacement devenu sans objet : suppression de la copie restante.</summary>
    public OperationResult ExecuteRetire(RetirePlan plan, Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        EnsureRealOperationsAllowed();
        var moved = plan.Moved;
        Log.Information("Nettoyage du déplacement de {Software} : suppression de {Destination}", moved.SoftwareName, moved.Destination);
        var hooks = new WindowsMoveHooks(new CriticalSectionOptions(), Processes, Data.DataRoot);
        var result = new RetireTransaction(Context(hooks)).Execute(moved.ToRetireRequest(plan.RemoveActiveJunction), progress, cancellationToken);
        LogResult(result);
        UpdateReadme(Path.GetDirectoryName(moved.Destination)!);
        return result;
    }

    /// <summary>Reprise des opérations interrompues (au démarrage).</summary>
    public IReadOnlyList<(StoredOperation Operation, OperationResult Result)> RecoverPending()
    {
        var service = new RecoveryService(Context(NoMoveHooks.Instance));
        var results = new List<(StoredOperation, OperationResult)>();
        foreach (var operation in service.PendingOperations())
        {
            var result = service.Recover(operation);
            LogResult(result);
            results.Add((operation, result));
        }

        return results;
    }

    /// <summary>Vérification d'intégrité d'un logiciel déplacé : la copie est comparée au manifeste vérifié lors du déplacement.</summary>
    public VerificationResult CheckIntegrity(MovedSoftware moved, CancellationToken cancellationToken)
    {
        var manifest = Store.LoadManifest(moved.Move.Id, OperationStore.VerifiedManifestFileName)
                       ?? throw new InvalidOperationException("Manifeste vérifié introuvable pour ce déplacement.");
        var guard = new PathGuard(Raw, ScopeFactory.Create(Raw, [moved.Destination]));
        return new TreeVerifier(guard).Verify(manifest, moved.Destination, new VerificationOptions { CheckDestinationIdentities = false }, cancellationToken: cancellationToken);
    }

    private void EnsureRealOperationsAllowed()
    {
        if (Settings.SimulationOnly || IsPreview)
        {
            throw new InvalidOperationException("Mode « Simulation uniquement » : aucune opération réelle n'est permise.");
        }
    }

    private void UpdateReadme(string destinationRoot)
    {
        try
        {
            ReadmeWriter.Update(destinationRoot, LoadMoves());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "LISEZMOI non mis à jour dans {Root}", destinationRoot);
        }
    }

    private void LogResult(OperationResult result)
    {
        MarkChanged();
        if (result.Error is not null)
        {
            Log.Warning(result.Error, "Opération {Id} ({Kind}) : {Outcome} — {Message}", result.OperationId, result.Kind, result.Outcome, result.Message);
        }
        else
        {
            Log.Information("Opération {Id} ({Kind}) : {Outcome} — {Message}", result.OperationId, result.Kind, result.Outcome, result.Message);
        }

        foreach (var warning in result.Warnings)
        {
            Log.Information("  avertissement : {Warning}", warning);
        }
    }
}
