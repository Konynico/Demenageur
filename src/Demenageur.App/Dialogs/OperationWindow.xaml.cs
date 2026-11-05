using System.ComponentModel;
using System.Windows.Interop;
using Demenageur.App.ViewModels;
using Demenageur.Windows.SystemServices;
using Wpf.Ui.Controls;

namespace Demenageur.App.Dialogs;

public partial class OperationWindow : FluentWindow
{
    private readonly OperationViewModel _viewModel;
    private bool _shutdownBlocked;

    public OperationWindow(OperationViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += Close;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationViewModel.Step))
        {
            return;
        }

        // Pendant l'exécution, Windows affiche un message si l'on tente d'éteindre l'ordinateur.
        var handle = new WindowInteropHelper(this).Handle;
        if (_viewModel.IsRunningStep && !_shutdownBlocked)
        {
            ShutdownBlocker.Block(handle, "Déménageur déplace un logiciel : attends la fin de l'opération.");
            _shutdownBlocked = true;
        }
        else if (!_viewModel.IsRunningStep && _shutdownBlocked)
        {
            ShutdownBlocker.Release(handle);
            _shutdownBlocked = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunningStep)
        {
            e.Cancel = true; // on ne ferme pas pendant une opération
            return;
        }

        base.OnClosing(e);
    }
}
