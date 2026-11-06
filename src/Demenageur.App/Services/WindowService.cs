using System.IO;
using System.Windows;
using Demenageur.App.Dialogs;
using Demenageur.App.ViewModels;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Windows.AppData;
using Microsoft.Extensions.DependencyInjection;

namespace Demenageur.App.Services;

/// <summary>Ouverture des fenêtres (assistant, démo, sélection de dossier).</summary>
public sealed class WindowService : IWindowService
{
    private readonly Engine _engine;
    private readonly IServiceProvider _services;

    public WindowService(Engine engine, IServiceProvider services)
    {
        _engine = engine;
        _services = services;
    }

    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    private IReadOnlyList<DriveViewModel> Drives => _services.GetRequiredService<MainViewModel>().Drives.ToList();

    public bool ShowMoveWizard(MoveCandidate candidate, IReadOnlyList<InstalledSoftware> catalog)
        => ShowWizard(new OperationViewModel(_engine, candidate, catalog, Drives));

    public bool ShowUndoWizard(MovedSoftware moved) => ShowSimulatedWizard(new OperationViewModel(_engine, moved));

    public bool ShowRetireWizard(MovedSoftware moved) => ShowSimulatedWizard(new OperationViewModel(_engine, moved, retire: true));

    private static bool ShowSimulatedWizard(OperationViewModel viewModel)
    {
        var window = new OperationWindow(viewModel) { Owner = Owner };
        window.Loaded += async (_, _) => await viewModel.SimulateAsync();
        window.ShowDialog();
        return viewModel.Changed;
    }

    private static bool ShowWizard(OperationViewModel viewModel)
    {
        var window = new OperationWindow(viewModel) { Owner = Owner };
        window.ShowDialog();
        return viewModel.Changed;
    }

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public void ShowDemo()
    {
        var drives = Drives;
        var picker = new DemoWindow(drives) { Owner = Owner };
        if (picker.ShowDialog() != true || picker.Source is null || picker.Destination is null)
        {
            return;
        }

        string folder;
        try
        {
            folder = DemoSoftwareFactory.Create(picker.Source.Letter + "\\");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner!, "Impossible de créer le logiciel fictif : " + ex.Message, "Déménageur", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var candidate = new MoveCandidate(folder, DemoSoftwareFactory.SoftwareName, "Démo Déménageur", [], FolderSource.Manual);
        var viewModel = new OperationViewModel(_engine, candidate, [], drives.Where(d => d.Letter == picker.Destination.Letter).ToList())
        {
            DestinationRoot = DemoSoftwareFactory.DestinationRoot(picker.Destination.Letter + "\\"),
        };
        if (ShowWizard(viewModel))
        {
            _ = _services.GetRequiredService<MovesViewModel>().LoadAsync();
            _ = _services.GetRequiredService<MainViewModel>().RefreshDrivesAsync();
        }
    }
}
