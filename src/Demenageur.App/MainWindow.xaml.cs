using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Demenageur.App.Services;
using Demenageur.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Demenageur.App;

public partial class MainWindow : FluentWindow
{
    private readonly Engine _engine;

    public MainWindow(MainViewModel viewModel, Engine engine)
    {
        _engine = engine;
        DataContext = viewModel;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) =>
        {
            _ = viewModel.RefreshDrivesAsync();
            foreach (var page in viewModel.Pages)
            {
                switch (page.Page)
                {
                    case SoftwareListViewModel software:
                        _ = software.LoadAsync();
                        break;
                    case MovesViewModel moves:
                        _ = moves.LoadAsync();
                        break;
                    case JournalViewModel journal:
                        _ = journal.LoadAsync();
                        break;
                }
            }
        };
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_engine.OperationInProgress)
        {
            e.Cancel = true;
            System.Windows.MessageBox.Show(this,
                "Une opération est en cours : attends qu'elle se termine (ou annule-la) avant de fermer Déménageur.",
                "Déménageur", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
