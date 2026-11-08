using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demenageur.App.Services;
using Demenageur.Core.Abstractions;
using Demenageur.Core.Inventory;
using Demenageur.Core.Journal;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Windows.AppData;

namespace Demenageur.App.ViewModels;

/// <summary>Logiciel déplacé, avec son état de santé.</summary>
public sealed partial class MovedItemViewModel : ObservableObject
{
    public MovedItemViewModel(MovedSoftware moved)
    {
        Moved = moved;
    }

    public MovedSoftware Moved { get; }

    public string Name => Moved.SoftwareName;

    public string Source => Moved.Source;

    public string Destination => Moved.Destination;

    public string Date => Moved.Move.Started?.LocalDateTime.ToString("dd/MM/yyyy HH:mm") ?? string.Empty;

    public bool IsHealthy => Moved.Health is MoveHealth.Ok or MoveHealth.CleanupPending;

    public RuleLevel Level => Moved.Health switch
    {
        MoveHealth.Ok => RuleLevel.Info,
        MoveHealth.CleanupPending or MoveHealth.SourceMissing or MoveHealth.ReplacedByFolder or MoveHealth.DestinationEmpty => RuleLevel.Warning,
        _ => RuleLevel.Block,
    };

    public string HealthText => Moved.Health switch
    {
        MoveHealth.Ok => "En bonne santé",
        MoveHealth.CleanupPending => "Nettoyage en attente",
        MoveHealth.JunctionBroken => "Jonction cassée",
        MoveHealth.ReplacedByFolder => "Remplacé par un dossier (mise à jour ?)",
        MoveHealth.DestinationMissing => "Copie introuvable",
        MoveHealth.DestinationEmpty => "Copie vide",
        MoveHealth.SourceMissing => "Désinstallé",
        _ => Moved.Health.ToString(),
    };

    public string HealthMessage => Moved.HealthMessage;

    [ObservableProperty]
    private string integrity = string.Empty;

    /// <summary>Taille actuelle de la copie (null : calcul en cours).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    private long? copySize;

    /// <summary>Jonction en place : la copie, c'est l'espace libéré sur le disque d'origine.</summary>
    public bool FreesSource => Moved.JunctionActive;

    /// <summary>Copie restée seule (désinstallation, dossier recréé par une mise à jour…) : elle ne libère plus rien.</summary>
    public bool IsOrphanCopy => !FreesSource && Moved.Health is not MoveHealth.DestinationMissing;

    public bool ShowsSize => FreesSource || IsOrphanCopy;

    public bool HasSize => CopySize is > 0;

    public string SourceDrive => DriveOf(Source);

    public string DestinationDrive => DriveOf(Destination);

    public string SizeCaption => FreesSource ? $"libérés sur {SourceDrive}" : $"occupés sur {DestinationDrive} (copie orpheline)";

    internal static string DriveOf(string path) => path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : path;
}

public sealed partial class MovesViewModel : ObservableObject, INavigationAware
{
    private readonly Engine _engine;
    private readonly IWindowService _windows;
    private CancellationTokenSource? _sizes;

    /// <summary>Tailles déjà calculées (par opération), affichées aussitôt au rechargement suivant puis recalculées.</summary>
    private readonly Dictionary<string, long> _knownSizes = new(StringComparer.Ordinal);

    /// <summary>« Espace libéré grâce à tes déplacements : 3,4 Go sur C: » (vide tant que rien n'est calculé).</summary>
    [ObservableProperty]
    private string freedSummary = string.Empty;

    /// <summary>« 435 Mo de copies orphelines sur D: : « Nettoyer la copie… » les supprime. »</summary>
    [ObservableProperty]
    private string orphanSummary = string.Empty;

    public MovesViewModel(Engine engine, IWindowService windows)
    {
        _engine = engine;
        _windows = windows;
    }

    public event Func<Task>? RefreshDrivesRequested;

    public ObservableCollection<MovedItemViewModel> Items { get; } = [];

    /// <summary>Opérations interrompues qui attendent une décision.</summary>
    public ObservableCollection<string> Attention { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(CheckIntegrityCommand), nameof(RetireCommand))]
    private MovedItemViewModel? selected;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>Quelques journaux et un contrôle de santé par déplacement (rapide) : rechargé à chaque arrivée sur la page.</summary>
    public Task OnNavigatedToAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var previousId = Selected?.Moved.Move.Id;
        var integrity = Items.Where(i => i.Integrity.Length > 0).ToDictionary(i => i.Moved.Move.Id, i => i.Integrity);
        try
        {
            var (moves, pending) = await Task.Run(() => (_engine.LoadMoves(), new RecoveryService(new MoveEngineContext
            {
                FileSystem = _engine.Raw,
                ScopeFactory = _engine.ScopeFactory,
                Store = _engine.Store,
            }).PendingOperations()));
            Items.Clear();
            foreach (var move in moves)
            {
                // Le résultat d'un contrôle d'intégrité déjà fait reste affiché.
                Items.Add(new MovedItemViewModel(move)
                {
                    Integrity = integrity.GetValueOrDefault(move.Move.Id, string.Empty),
                    CopySize = _knownSizes.TryGetValue(move.Move.Id, out var known) ? known : null,
                });
            }

            Selected = Items.FirstOrDefault(i => i.Moved.Move.Id == previousId);
            UpdateSpaceSummary();
            ComputeSizesAsync().LogFailures("tailles des déplacements");

            Attention.Clear();
            foreach (var operation in pending.Where(o => !o.IsCommitted || o.LastStep == JournalSteps.NeedsAttention))
            {
                Attention.Add($"{operation.SoftwareName} ({operation.Id}) : {operation.Journal.Last?.Message ?? operation.LastStep}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Taille de chaque copie, en arrière-plan (les gros jeux peuvent prendre quelques secondes).</summary>
    private async Task ComputeSizesAsync()
    {
        _sizes?.Cancel();
        _sizes = new CancellationTokenSource();
        var token = _sizes.Token;
        var calculator = new FolderSizeCalculator(_engine.Raw);
        foreach (var item in Items.Where(i => i.ShowsSize).ToList())
        {
            var bytes = await Task.Run(() =>
            {
                try
                {
                    return calculator.Compute(item.Destination, token).Bytes;
                }
                catch (Exception ex) when (ex is Core.FileSystem.FileSystemException or OperationCanceledException)
                {
                    return -1L;
                }
            }, CancellationToken.None);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (bytes >= 0)
            {
                item.CopySize = bytes;
                _knownSizes[item.Moved.Move.Id] = bytes;
            }

            UpdateSpaceSummary();
        }
    }

    private void UpdateSpaceSummary()
    {
        static string PerDrive(IEnumerable<(string Drive, long Bytes)> sizes)
            => string.Join(", ", sizes.GroupBy(s => s.Drive).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{MovePlanner.Bytes(g.Sum(s => s.Bytes))} sur {g.Key}"));

        var freed = Items.Where(i => i.FreesSource && i.CopySize is > 0).Select(i => (i.SourceDrive, i.CopySize!.Value)).ToList();
        var orphans = Items.Where(i => i.IsOrphanCopy && i.CopySize is > 0).Select(i => (i.DestinationDrive, i.CopySize!.Value)).ToList();
        var pending = Items.Any(i => i.ShowsSize && i.CopySize is null) ? " (calcul en cours…)" : string.Empty;
        FreedSummary = freed.Count == 0 ? string.Empty : $"Espace libéré grâce à tes déplacements : {PerDrive(freed)}{pending}";
        OrphanSummary = orphans.Count == 0 ? string.Empty
            : $"Copies orphelines : {PerDrive(orphans)}. « Nettoyer la copie… » les supprime.";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UndoAsync()
    {
        if (Selected is null)
        {
            return;
        }

        if (_windows.ShowUndoWizard(Selected.Moved))
        {
            await LoadAsync();
            if (RefreshDrivesRequested is not null)
            {
                await RefreshDrivesRequested();
            }
        }
    }

    /// <summary>Nettoyage de la copie restante (logiciel désinstallé ou remplacé). La simulation dit si c'est permis.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RetireAsync()
    {
        if (Selected is null)
        {
            return;
        }

        if (_windows.ShowRetireWizard(Selected.Moved))
        {
            await LoadAsync();
            if (RefreshDrivesRequested is not null)
            {
                await RefreshDrivesRequested();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CheckIntegrityAsync()
    {
        var item = Selected;
        if (item is null)
        {
            return;
        }

        item.Integrity = "Vérification en cours (relecture complète)…";
        try
        {
            var result = await Task.Run(() => _engine.CheckIntegrity(item.Moved, CancellationToken.None));
            item.Integrity = result.Success
                ? $"Intègre : {result.ItemsChecked} éléments identiques à ceux vérifiés lors du déplacement."
                : $"{result.Errors.Count()} différence(s) depuis le déplacement (normal si le logiciel s'est mis à jour). Première : {result.Errors.First().RelativePath} — {result.Errors.First().Message}";
        }
        catch (Exception ex)
        {
            item.Integrity = "Vérification impossible : " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenSource(MovedItemViewModel? item) => ShellActions.OpenFolder((item ?? Selected)?.Source ?? string.Empty);

    [RelayCommand]
    private void OpenDestination(MovedItemViewModel? item) => ShellActions.OpenFolder((item ?? Selected)?.Destination ?? string.Empty);
}

/// <summary>Opération du journal.</summary>
public sealed record OperationLine(string Id, string Title, string State, string Date, IReadOnlyList<JournalRecord> Records);

public sealed partial class JournalViewModel : ObservableObject, INavigationAware
{
    private readonly Engine _engine;
    private bool _loading;

    public JournalViewModel(Engine engine)
    {
        _engine = engine;
    }

    public ObservableCollection<OperationLine> Operations { get; } = [];

    [ObservableProperty]
    private OperationLine? selected;

    public string DataFolder => _engine.Data.DataRoot;

    /// <summary>Lecture des journaux seulement (rapide) : rechargé à chaque arrivée sur la page.</summary>
    public Task OnNavigatedToAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadCoreAsync()
    {
        var operations = await Task.Run(() => _engine.Store.List());
        // Garde l'opération choisie, sauf si c'était la plus récente : on suit alors la nouvelle plus récente.
        var previous = Selected is { } s && Operations.IndexOf(s) > 0 ? s.Id : null;
        Operations.Clear();
        foreach (var operation in operations.OrderByDescending(o => o.Id, StringComparer.Ordinal))
        {
            var kind = operation.Kind switch
            {
                OperationKind.Move => "Déplacement",
                OperationKind.Undo => "Annulation",
                _ => "Nettoyage",
            };
            var state = !operation.Trusted ? "journal non fiable (ignoré)"
                : operation.Journal.Corrupt ? "journal corrompu"
                : operation.LastStep switch
                {
                    JournalSteps.Completed => "terminé",
                    JournalSteps.RolledBack => "revenu à l'état initial",
                    JournalSteps.CleanupPending => "nettoyage en attente",
                    JournalSteps.NeedsAttention => "intervention nécessaire",
                    null => "vide",
                    _ => "interrompu",
                };
            Operations.Add(new OperationLine(operation.Id, $"{kind} — {operation.SoftwareName}", state, operation.Started?.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") ?? string.Empty, operation.Journal.Records));
        }

        Selected = Operations.FirstOrDefault(o => o.Id == previous) ?? Operations.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenLogs() => ShellActions.OpenFolder(_engine.Data.LogsDirectory);

    [RelayCommand]
    private void OpenOperations() => ShellActions.OpenFolder(_engine.Data.OperationsDirectory);
}

public sealed partial class HelpViewModel : ObservableObject
{
    private readonly IWindowService _windows;

    public HelpViewModel(IWindowService windows)
    {
        _windows = windows;
    }

    [RelayCommand]
    private void StartDemo() => _windows.ShowDemo();
}

public sealed partial class SettingsViewModel : ObservableObject, INavigationAware
{
    private readonly Engine _engine;

    private readonly IWindowService _windows;

    public SettingsViewModel(Engine engine, IWindowService windows)
    {
        _engine = engine;
        _windows = windows;
        RefreshExclusions();
        engine.ExclusionsChanged += () => System.Windows.Application.Current?.Dispatcher.Invoke(RefreshExclusions);
        simulationOnly = engine.Settings.SimulationOnly;
        restorePointByDefault = engine.Settings.RestorePointByDefault;
        destinationFolderName = engine.Settings.DestinationFolderName;
        RefreshRestorePointText();
    }

    /// <summary>Protection système et lanceurs relus à chaque arrivée sur la page (lectures seules, rapides).</summary>
    public async Task OnNavigatedToAsync()
    {
        RefreshRestorePointText();
        await ScanLaunchersCoreAsync(() => Demenageur.Windows.Safety.LauncherInspector.ReadAll());
    }

    private void RefreshRestorePointText()
    {
        RestorePointText = _engine.RestorePoints.GetAvailability() switch
        {
            RestorePointAvailability.Available => "La protection système est active : un point de restauration peut être créé avant chaque déplacement.",
            RestorePointAvailability.ProtectionDisabled => "La protection système est DÉSACTIVÉE sur le disque de Windows : aucun point de restauration ne peut être créé. Déménageur ne l'active jamais lui-même ; tu peux le faire dans le panneau Windows (cela réserve de l'espace sur C:).",
            _ => "État de la protection système inconnu.",
        };
    }

    [ObservableProperty]
    private string restorePointText = string.Empty;

    public string DataFolder => _engine.Data.DataRoot;

    public bool IsPreview => _engine.IsPreview;

    [ObservableProperty]
    private bool simulationOnly;

    [ObservableProperty]
    private bool restorePointByDefault;

    [ObservableProperty]
    private string destinationFolderName;

    [ObservableProperty]
    private string savedMessage = string.Empty;

    [RelayCommand]
    private void Save()
    {
        var name = DestinationFolderName.Trim();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            SavedMessage = "Nom de dossier invalide.";
            return;
        }

        _engine.Settings.SimulationOnly = SimulationOnly || _engine.IsPreview;
        _engine.Settings.RestorePointByDefault = RestorePointByDefault;
        _engine.Settings.DestinationFolderName = name;
        if (!_engine.IsPreview)
        {
            _engine.SaveSettings();
        }

        SavedMessage = $"Paramètres enregistrés ({DateTime.Now:HH:mm:ss}).";
    }

    [RelayCommand]
    private void OpenSystemProtection() => _engine.RestorePoints.OpenSystemProtectionSettings();

    [RelayCommand]
    private void OpenDataFolder() => ShellActions.OpenFolder(_engine.Data.DataRoot);

    // ── Historique (données personnelles : droit à l'effacement) ──

    [RelayCommand]
    private async Task DeleteHistoryAsync()
    {
        try
        {
            var deletable = await Task.Run(_engine.FindDeletableHistory);
            if (deletable.Count == 0)
            {
                _windows.Inform("Historique", "Rien à effacer : il ne reste que les journaux des logiciels encore déplacés (indispensables pour les annuler) et des opérations en cours.");
                return;
            }

            static string KindOf(OperationKind kind) => kind switch
            {
                OperationKind.Move => "déplacement",
                OperationKind.Undo => "annulation",
                OperationKind.Retire => "nettoyage",
                _ => "opération",
            };
            var names = string.Join("\n", deletable.Take(15).Select(o => $"• {o.SoftwareName} ({KindOf(o.Kind)}, {o.Started:dd/MM/yyyy})"));
            var more = deletable.Count > 15 ? $"\n… et {deletable.Count - 15} autre(s)." : string.Empty;
            if (!_windows.Confirm("Effacer l'historique", $"Effacer définitivement les journaux de {deletable.Count} opération(s) terminée(s) ?\n\n{names}{more}\n\n"
                    + "Les journaux des logiciels encore déplacés sont conservés. Les logs techniques (60 jours) ne sont pas concernés."))
            {
                return;
            }

            var (deleted, failed) = await Task.Run(_engine.DeleteFinishedHistory);
            HistoryMessage = failed == 0 ? $"{deleted} opération(s) effacée(s) de l'historique." : $"{deleted} opération(s) effacée(s), {failed} n'ont pas pu l'être (voir les logs).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or Core.FileSystem.FileSystemException)
        {
            HistoryMessage = "Effacement impossible : " + ex.Message;
        }
    }

    [ObservableProperty]
    private string historyMessage = string.Empty;

    // ── Exclusions manuelles ──

    public ObservableCollection<UserExclusion> Exclusions { get; } = [];

    private void RefreshExclusions()
    {
        Exclusions.Clear();
        foreach (var exclusion in _engine.Settings.Exclusions.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            Exclusions.Add(exclusion);
        }
    }

    [RelayCommand]
    private void AddExclusion()
    {
        if (_windows.PickFolder("Dossier à ne jamais déplacer (lui et tout ce qu'il contient)") is { } folder)
        {
            _engine.AddExclusion(folder, null);
        }
    }

    [RelayCommand]
    private void RemoveExclusion(UserExclusion? exclusion)
    {
        if (exclusion is not null)
        {
            _engine.RemoveExclusion(exclusion);
        }
    }

    // ── Lanceurs détectés ──

    public ObservableCollection<string> DetectedLaunchers { get; } = [];

    [ObservableProperty]
    private string launchersStatus = "Les bibliothèques de jeux sont listées à l'ouverture de cette page.";

    [RelayCommand]
    private Task ScanLaunchersAsync() => ScanLaunchersCoreAsync(() =>
    {
        // Bouton « Analyser » : capture système complète, dont profitera aussi la prochaine analyse des logiciels.
        _engine.RefreshSystem();
        return _engine.System.LauncherLocations;
    });

    private bool _scanning;

    private async Task ScanLaunchersCoreAsync(Func<IReadOnlyList<ManagedLocation>> read)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        try
        {
            await ScanLaunchersOnceAsync(read);
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task ScanLaunchersOnceAsync(Func<IReadOnlyList<ManagedLocation>> read)
    {
        LaunchersStatus = "Analyse en cours…";
        var locations = await Task.Run(read);
        DetectedLaunchers.Clear();
        foreach (var group in locations.GroupBy(l => l.LauncherId))
        {
            var launcher = KnownLaunchers.Get(group.Key);
            var support = launcher?.Support switch
            {
                LauncherMoveSupport.NativeMove => "sait déplacer ses jeux",
                LauncherMoveSupport.Relocate => "sait retrouver un jeu copié ailleurs",
                _ => "pas de déplacement documenté",
            };
            DetectedLaunchers.Add($"{launcher?.Name ?? group.Key} ({support}) :");
            foreach (var location in group.OrderBy(l => l.Path, StringComparer.CurrentCultureIgnoreCase))
            {
                DetectedLaunchers.Add($"    {location.Path}" + (location.Title is { } title && !location.Path.EndsWith(title, StringComparison.OrdinalIgnoreCase) ? $"  ({title})" : string.Empty));
            }
        }

        LaunchersStatus = locations.Count == 0
            ? "Aucune bibliothèque trouvée."
            : $"{locations.Count} emplacement(s) trouvé(s). Battle.net, EA, Riot et Rockstar sont reconnus d'après les logiciels installés.";
    }
}

/// <summary>Fenêtres et boîtes de dialogue (implémenté par l'application).</summary>
public interface IWindowService
{
    bool ShowMoveWizard(MoveCandidate candidate, IReadOnlyList<InstalledSoftware> catalog);

    bool ShowUndoWizard(MovedSoftware moved);

    bool ShowRetireWizard(MovedSoftware moved);

    string? PickFolder(string title);

    bool Confirm(string title, string message);

    void Inform(string title, string message);

    void ShowDemo();
}
