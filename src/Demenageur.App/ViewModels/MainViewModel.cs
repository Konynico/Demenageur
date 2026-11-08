using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demenageur.App.Services;
using Demenageur.Core.Safety;

namespace Demenageur.App.ViewModels;

/// <summary>Disque affiché dans le bandeau.</summary>
public sealed class DriveViewModel
{
    public DriveViewModel(VolumeDetails details)
    {
        Details = details;
        Letter = details.Letter;
        Label = string.IsNullOrWhiteSpace(details.Volume.Label) ? "Disque local" : details.Volume.Label;
        Total = details.Volume.TotalBytes;
        Free = details.Volume.FreeBytesAvailable;
        UsedRatio = Total > 0 ? (double)(Total - Free) / Total : 0;
        Description = $"{details.Volume.FileSystemName} · {details.BusDescription}{(details.IsSystemVolume ? " · Windows" : string.Empty)}";
    }

    public VolumeDetails Details { get; }

    public string Letter { get; }

    public string Label { get; }

    public long Total { get; }

    public long Free { get; }

    public double UsedRatio { get; }

    public double UsedPercent => UsedRatio * 100;

    public string Description { get; }

    public string FreeText => $"{MovePlanner.Bytes(Free)} libres sur {MovePlanner.Bytes(Total)}";
}

/// <summary>Page qui se met à jour quand on y arrive (le bouton « Actualiser » reste là pour ce qui change pendant qu'on la regarde).</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync();
}

/// <summary>Élément de navigation.</summary>
public sealed record PageItem(string Title, Wpf.Ui.Controls.SymbolRegular Icon, ObservableObject Page);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Engine _engine;

    public MainViewModel(Engine engine, SoftwareListViewModel software, MovesViewModel moves, JournalViewModel journal, HelpViewModel help, SettingsViewModel settings)
    {
        _engine = engine;
        Pages =
        [
            new PageItem("Logiciels", Wpf.Ui.Controls.SymbolRegular.Apps24, software),
            new PageItem("Mes déplacements", Wpf.Ui.Controls.SymbolRegular.ArrowSwap24, moves),
            new PageItem("Journal", Wpf.Ui.Controls.SymbolRegular.History24, journal),
            new PageItem("Aide", Wpf.Ui.Controls.SymbolRegular.QuestionCircle24, help),
            new PageItem("Paramètres", Wpf.Ui.Controls.SymbolRegular.Settings24, settings),
        ];
        selectedPage = Pages[0];
        software.RefreshDrivesRequested += async () => await RefreshDrivesAsync();
        moves.RefreshDrivesRequested += async () => await RefreshDrivesAsync();
    }

    public ObservableCollection<PageItem> Pages { get; }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public ObservableCollection<string> StartupMessages { get; } = [];

    [ObservableProperty]
    private PageItem selectedPage;

    public bool IsSimulationOnly => _engine.Settings.SimulationOnly;

    public bool IsPreview => _engine.IsPreview;

    public string Title => _engine.IsPreview ? "Déménageur — aperçu (simulation uniquement)" : "Déménageur";

    public void Navigate(ObservableObject page)
    {
        SelectedPage = Pages.First(p => p.Page == page);
    }

    partial void OnSelectedPageChanged(PageItem value)
    {
        RunInBackground(RefreshDrivesAsync, null);
        if (value?.Page is INavigationAware page)
        {
            RunInBackground(page.OnNavigatedToAsync, value.Title);
        }
    }

    /// <summary>Lance une actualisation sans bloquer la navigation ; une erreur est journalisée, jamais ignorée en silence.</summary>
    private static async void RunInBackground(Func<Task> refresh, string? pageTitle)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await refresh();
            if (pageTitle is not null)
            {
                Serilog.Log.Information("Arrivée sur « {Page} » : actualisée en {Milliseconds} ms", pageTitle, clock.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Actualisation à l'arrivée sur une page");
        }
    }

    [RelayCommand]
    public async Task RefreshDrivesAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var volumes = await Task.Run(() => _engine.Volumes.ListVolumes());
        Serilog.Log.Information("Disques : {Count} en {Seconds:F1} s", volumes.Count, clock.Elapsed.TotalSeconds);
        Drives.Clear();
        foreach (var volume in volumes.OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase))
        {
            Drives.Add(new DriveViewModel(volume));
        }

        OnPropertyChanged(nameof(IsSimulationOnly));
    }
}
