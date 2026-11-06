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
}

public sealed partial class MovesViewModel : ObservableObject
{
    private readonly Engine _engine;
    private readonly IWindowService _windows;

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

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
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
                Items.Add(new MovedItemViewModel(move));
            }

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

public sealed partial class JournalViewModel : ObservableObject
{
    private readonly Engine _engine;

    public JournalViewModel(Engine engine)
    {
        _engine = engine;
    }

    public ObservableCollection<OperationLine> Operations { get; } = [];

    [ObservableProperty]
    private OperationLine? selected;

    public string DataFolder => _engine.Data.DataRoot;

    [RelayCommand]
    public async Task LoadAsync()
    {
        var operations = await Task.Run(() => _engine.Store.List());
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

        Selected = Operations.FirstOrDefault();
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

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Engine _engine;

    public SettingsViewModel(Engine engine)
    {
        _engine = engine;
        simulationOnly = engine.Settings.SimulationOnly;
        restorePointByDefault = engine.Settings.RestorePointByDefault;
        destinationFolderName = engine.Settings.DestinationFolderName;
        RestorePointText = engine.RestorePoints.GetAvailability() switch
        {
            RestorePointAvailability.Available => "La protection système est active : un point de restauration peut être créé avant chaque déplacement.",
            RestorePointAvailability.ProtectionDisabled => "La protection système est DÉSACTIVÉE sur le disque de Windows : aucun point de restauration ne peut être créé. Déménageur ne l'active jamais lui-même ; tu peux le faire dans le panneau Windows (cela réserve de l'espace sur C:).",
            _ => "État de la protection système inconnu.",
        };
    }

    public string RestorePointText { get; }

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
}

/// <summary>Fenêtres et boîtes de dialogue (implémenté par l'application).</summary>
public interface IWindowService
{
    bool ShowMoveWizard(MoveCandidate candidate, IReadOnlyList<InstalledSoftware> catalog);

    bool ShowUndoWizard(MovedSoftware moved);

    bool ShowRetireWizard(MovedSoftware moved);

    string? PickFolder(string title);

    void ShowDemo();
}
