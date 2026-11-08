using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demenageur.App.Services;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Safety;

namespace Demenageur.App.ViewModels;

public enum SoftwareStatus
{
    Movable,
    NeedsConfirmation,
    Blocked,
    AlreadyMoved,
    UnknownFolder,

    /// <summary>Géré par un lanceur qui sait le déplacer lui-même (Steam, Xbox) : à préférer.</summary>
    UseLauncher,
}

/// <summary>Ligne de la liste des logiciels.</summary>
public sealed partial class SoftwareItemViewModel : ObservableObject
{
    public SoftwareItemViewModel(InstalledSoftware software)
    {
        Software = software;
    }

    public InstalledSoftware Software { get; }

    public string Name => Software.DisplayName;

    public string Publisher => Software.Publisher ?? string.Empty;

    public string Version => Software.Version ?? string.Empty;

    public string Folder => Software.Folder.Path ?? "(dossier inconnu)";

    public string Drive => Software.Folder.Path is { Length: >= 2 } p ? p[..2].ToUpperInvariant() : "—";

    public bool IsHidden => Software.IsHidden;

    public string FolderNote => Software.Folder.NeedsConfirmation ? "dossier déduit (à confirmer)" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeSortKey))]
    private long? size;

    public long SizeSortKey => Size ?? -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SoftwareStatus status = SoftwareStatus.Movable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LauncherText), nameof(IsLauncherManaged))]
    private IReadOnlyList<RuleVerdict> verdicts = [];

    /// <summary>« Géré par Steam », « Géré par Epic Games »… (vide sinon).</summary>
    public string LauncherText => Verdicts.FirstOrDefault(v => v.RuleId == SafetyRules.LauncherRuleId)?.Title ?? string.Empty;

    public bool IsLauncherManaged => LauncherText.Length > 0;

    public string StatusText => Status switch
    {
        SoftwareStatus.Movable => "Déplaçable",
        SoftwareStatus.NeedsConfirmation => "Confirmation requise",
        SoftwareStatus.Blocked => "Bloqué",
        SoftwareStatus.AlreadyMoved => "Déjà déplacé",
        SoftwareStatus.UseLauncher => "Via son lanceur",
        _ => "Dossier inconnu",
    };

    public MoveCandidate ToCandidate() => new(Software.Folder.Path!, Software.DisplayName, Software.Publisher, Software.Entries, Software.Folder.Source);
}

public sealed partial class SoftwareListViewModel : ObservableObject, INavigationAware
{
    /// <summary>Au-delà, l'inventaire est relu en arrivant sur la page (logiciels installés ou désinstallés entre-temps).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly Engine _engine;
    private readonly IWindowService _windows;
    private CancellationTokenSource? _sizes;
    private bool _driveFilterInitialized;
    private int _loadedChangeCount = -1;
    private DateTime _loadedAt = DateTime.MinValue;

    public SoftwareListViewModel(Engine engine, IWindowService windows)
    {
        _engine = engine;
        _windows = windows;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(SoftwareItemViewModel.SizeSortKey), ListSortDirection.Descending));
        showHidden = engine.Settings.ShowHiddenEntries;
        hideLauncherManaged = engine.Settings.HideLauncherManaged;
    }

    public event Func<Task>? RefreshDrivesRequested;

    public ObservableCollection<SoftwareItemViewModel> Items { get; } = [];

    public ICollectionView View { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool showHidden;

    [ObservableProperty]
    private bool onlyMovable;

    /// <summary>Masquer les jeux gérés par un lanceur (Steam, Epic, Xbox…).</summary>
    [ObservableProperty]
    private bool hideLauncherManaged;

    partial void OnHideLauncherManagedChanged(bool value)
    {
        _engine.Settings.HideLauncherManaged = value;
        View.Refresh();
    }

    /// <summary>Filtre par disque (« Tous les disques » ou une lettre). Par défaut : le disque de Windows, celui qu'on veut libérer.</summary>
    public ObservableCollection<string> DriveFilters { get; } = [AllDrives];

    public const string AllDrives = "Tous les disques";

    [ObservableProperty]
    private string driveFilter = AllDrives;

    partial void OnDriveFilterChanged(string value) => View.Refresh();

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string loadingText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedCommand), nameof(ExcludeSelectedCommand))]
    private SoftwareItemViewModel? selected;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnShowHiddenChanged(bool value)
    {
        _engine.Settings.ShowHiddenEntries = value;
        View.Refresh();
    }

    partial void OnOnlyMovableChanged(bool value) => View.Refresh();

    private bool Filter(object item)
    {
        var software = (SoftwareItemViewModel)item;
        if (!ShowHidden && software.IsHidden)
        {
            return false;
        }

        if (OnlyMovable && software.Status is not (SoftwareStatus.Movable or SoftwareStatus.NeedsConfirmation))
        {
            return false;
        }

        if (HideLauncherManaged && software.IsLauncherManaged)
        {
            return false;
        }

        if (DriveFilter != AllDrives && !string.Equals(software.Drive, DriveFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SearchText.Length == 0
               || software.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
               || software.Publisher.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
               || software.Folder.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// L'inventaire complet (registre, règles, puis taille de chaque dossier) est coûteux : il n'est relu en arrivant
    /// sur la page que s'il est périmé (opération ou exclusion depuis le dernier chargement, ou plus de 5 minutes).
    /// </summary>
    public Task OnNavigatedToAsync()
        => _engine.ChangeCount != _loadedChangeCount || DateTime.UtcNow - _loadedAt > MaxAge ? LoadAsync() : Task.CompletedTask;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        _sizes?.Cancel();
        IsLoading = true;
        _loadedChangeCount = _engine.ChangeCount;
        _loadedAt = DateTime.UtcNow;
        // Tailles déjà connues : affichées tout de suite, puis recalculées en arrière-plan.
        var knownSizes = Items.Where(i => i.Size is not null && i.Software.Folder.Path is not null)
            .GroupBy(i => i.Software.Folder.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Size, StringComparer.OrdinalIgnoreCase);
        var previousId = Selected?.Software.Id;
        LoadingText = "Lecture du registre et analyse des logiciels…";
        try
        {
            var (catalog, statuses) = await Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _engine.RefreshSystem();
                var catalog = _engine.Catalog.Load();
                var catalogTime = clock.Elapsed;
                var system = _engine.System;
                var systemTime = clock.Elapsed - catalogTime;
                var rules = _engine.CreateRules();
                var statuses = catalog.Select(s => Evaluate(s, rules, system, catalog)).ToList();
                Serilog.Log.Information("Inventaire : {Count} logiciels (registre {Catalog:F1} s, état système {System:F1} s, règles {Rules:F1} s)",
                    catalog.Count, catalogTime.TotalSeconds, systemTime.TotalSeconds, (clock.Elapsed - catalogTime - systemTime).TotalSeconds);
                return (catalog, statuses);
            });

            Items.Clear();
            for (var i = 0; i < catalog.Count; i++)
            {
                var item = new SoftwareItemViewModel(catalog[i]) { Status = statuses[i].Status, Verdicts = statuses[i].Verdicts };
                if (catalog[i].Folder.Path is { } path && knownSizes.TryGetValue(path, out var known))
                {
                    item.Size = known;
                }

                Items.Add(item);
            }

            Selected = previousId is null ? null : Items.FirstOrDefault(i => i.Software.Id == previousId);

            CatalogSnapshot = catalog;
            var previous = DriveFilter;
            foreach (var drive in Items.Select(i => i.Drive).Where(d => d != "—").Distinct().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!DriveFilters.Contains(drive))
                {
                    DriveFilters.Add(drive);
                }
            }

            if (!_driveFilterInitialized)
            {
                _driveFilterInitialized = true;
                var systemDrive = _engine.Known.SystemDrive.ToUpperInvariant();
                DriveFilter = DriveFilters.Contains(systemDrive) ? systemDrive : AllDrives;
            }
            else
            {
                DriveFilter = previous;
            }
        }
        finally
        {
            IsLoading = false;
        }

        ComputeSizesAsync().LogFailures("tailles des logiciels");
    }

    /// <summary>Dernier inventaire chargé (utilisé par la simulation pour repérer les logiciels imbriqués).</summary>
    public IReadOnlyList<InstalledSoftware> CatalogSnapshot { get; private set; } = [];

    private (SoftwareStatus Status, IReadOnlyList<RuleVerdict> Verdicts) Evaluate(InstalledSoftware software, SafetyRules rules, SystemSnapshot system, IReadOnlyList<InstalledSoftware> catalog)
    {
        if (software.Folder.Path is null)
        {
            return (SoftwareStatus.UnknownFolder, []);
        }

        try
        {
            if (_engine.Raw.GetEntry(software.Folder.Path) is { Kind: FsEntryKind.Link })
            {
                return (SoftwareStatus.AlreadyMoved, []);
            }

            var verdicts = rules.Evaluate(new MoveCandidate(software.Folder.Path, software.DisplayName, software.Publisher, software.Entries, software.Folder.Source), system, catalog);
            var status = verdicts.Any(v => v.Level == RuleLevel.Block) ? SoftwareStatus.Blocked
                : verdicts.Any(v => v.RuleId == SafetyRules.LauncherRuleId && v.Level == RuleLevel.Confirm) ? SoftwareStatus.UseLauncher
                : verdicts.Any(v => v.Level == RuleLevel.Confirm) ? SoftwareStatus.NeedsConfirmation
                : SoftwareStatus.Movable;
            return (status, verdicts);
        }
        catch (FileSystemException)
        {
            return (SoftwareStatus.UnknownFolder, []);
        }
    }

    private async Task ComputeSizesAsync()
    {
        _sizes = new CancellationTokenSource();
        var token = _sizes.Token;
        var calculator = new FolderSizeCalculator(_engine.Raw);
        foreach (var item in Items.Where(i => i.Software.Folder.Path is not null).ToList())
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var size = await Task.Run(() =>
            {
                try
                {
                    return calculator.Compute(item.Software.Folder.Path!, token).Bytes;
                }
                catch (Exception ex) when (ex is FileSystemException or OperationCanceledException)
                {
                    return -1L;
                }
            }, CancellationToken.None);
            item.Size = size;
        }

        View.Refresh();
    }

    private bool CanMoveSelected() => Selected is { Status: SoftwareStatus.Movable or SoftwareStatus.NeedsConfirmation or SoftwareStatus.UseLauncher };

    private bool HasFolder() => Selected?.Software.Folder.Path is not null;

    /// <summary>Ajoute le dossier du logiciel sélectionné aux exclusions (réversible dans Paramètres).</summary>
    [RelayCommand(CanExecute = nameof(HasFolder))]
    private async Task ExcludeSelectedAsync()
    {
        if (Selected?.Software.Folder.Path is not { } path)
        {
            return;
        }

        _engine.AddExclusion(path, Selected.Name);
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private async Task MoveSelectedAsync()
    {
        if (Selected is null)
        {
            return;
        }

        if (_windows.ShowMoveWizard(Selected.ToCandidate(), CatalogSnapshot) && RefreshDrivesRequested is not null)
        {
            await RefreshDrivesRequested();
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task MoveFolderAsync()
    {
        var folder = _windows.PickFolder("Choisis le dossier à déplacer (jeu, application portable…)");
        if (folder is null)
        {
            return;
        }

        var candidate = new MoveCandidate(folder.TrimEnd('\\'), System.IO.Path.GetFileName(folder.TrimEnd('\\')), null,
            CatalogSnapshot.Where(s => s.Folder.Path is { } p && Core.FileSystem.PathUtil.IsWithinIgnoreCase(p, folder)).SelectMany(s => s.Entries).ToList(),
            FolderSource.Manual);
        if (_windows.ShowMoveWizard(candidate, CatalogSnapshot) && RefreshDrivesRequested is not null)
        {
            await RefreshDrivesRequested();
            await LoadAsync();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected?.Software.Folder.Path is { } path)
        {
            Demenageur.Windows.AppData.ShellActions.OpenFolder(path);
        }
    }
}
