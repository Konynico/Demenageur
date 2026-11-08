using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Demenageur.App.Dialogs;
using Demenageur.App.Services;
using Demenageur.App.ViewModels;
using Demenageur.Core.Moving;
using Demenageur.Windows.SystemServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Demenageur.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Exceptions des tâches de fond : journalisées au lieu de disparaître (ou de fermer l'app sans trace).
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Exception non gérée");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Exception non observée dans une tâche de fond");
            args.SetObserved();
        };

        try
        {
            _instance = SingleInstance.Acquire();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            MessageBox.Show($"{SingleInstance.BlockedMessage}\n\n({ex.Message})", "Déménageur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_instance.State == InstanceState.Blocked)
        {
            MessageBox.Show(SingleInstance.BlockedMessage, "Déménageur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!_instance.IsFirstInstance)
        {
            if (AppHost.IsPreview && e.Args.FirstOrDefault() == "--capture")
            {
                // Capture hors écran (développement) : ne jamais afficher de fenêtre sur l'écran de l'utilisateur.
                Shutdown(3);
                return;
            }

            MessageBox.Show("Déménageur est déjà ouvert.", "Déménageur", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (!AppHost.IsPreview && !IsElevated())
        {
            // Le manifeste exige l'élévation ; on ne fait rien sans elle, par sécurité.
            MessageBox.Show("Déménageur doit être lancé en tant qu'administrateur.", "Déménageur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            _host = AppHost.Build(e.Args);
            await _host.StartAsync();

            // Toutes les fenêtres (dialogues compris) suivent le thème clair/sombre de Windows.
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            var engine = _host.Services.GetRequiredService<Engine>();

            // Avertissement du premier lancement.
            var demoRequested = false;

            // Aperçu uniquement : « --capture <page|avertissement> <fichier.png> » dessine l'interface hors écran puis quitte.
            var capture = AppHost.IsPreview && e.Args.Length >= 3 && e.Args[0] == "--capture" ? (Target: e.Args[1], File: e.Args[2]) : ((string Target, string File)?)null;
            var captureDelay = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var seconds) ? seconds * 1000 : 9000;
            if (capture is { Target: "avertissement" } warning)
            {
                var preview = new FirstRunWindow();
                PreviewCapture.PrepareOffscreen(preview);
                preview.Show();
                await PreviewCapture.CaptureAsync(preview, warning.File, 1500);
                Shutdown(0);
                return;
            }

            if (capture is { Target: "assistant" } wizardCapture)
            {
                // Logiciel fictif créé dans %TEMP% uniquement ; la simulation ne fait que lire.
                var mainVm = _host.Services.GetRequiredService<MainViewModel>();
                await mainVm.RefreshDrivesAsync();
                var folder = Demenageur.Windows.AppData.DemoSoftwareFactory.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Demenageur-Demo-Apercu"));
                var candidate = new Core.Safety.MoveCandidate(folder, "LogicielFictif", "Démo Déménageur", [], Core.Inventory.FolderSource.Manual);
                var wizardVm = new OperationViewModel(engine, candidate, [], mainVm.Drives.ToList());
                var wizard = new OperationWindow(wizardVm);
                PreviewCapture.PrepareOffscreen(wizard);
                wizard.Show();
                await wizardVm.SimulateAsync();
                wizardVm.Understood = true;
                await PreviewCapture.CaptureAsync(wizard, wizardCapture.File, 1000);
                Shutdown(0);
                return;
            }

            var previewPage = capture is { } c && int.TryParse(c.Target, out var page) ? page : (int?)null;
            if (previewPage is not null)
            {
                engine.Settings.RisksAccepted = true;
            }

            if (!engine.Settings.RisksAccepted)
            {
                var firstRun = new FirstRunWindow();
                if (firstRun.ShowDialog() != true)
                {
                    Shutdown(0);
                    return;
                }

                demoRequested = firstRun.DemoRequested;
                engine.Settings.RisksAccepted = true;
                if (!engine.IsPreview)
                {
                    engine.SaveSettings();
                }
            }

            // Reprise des opérations interrompues (crash, coupure de courant) avant tout le reste.
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            if (!engine.IsPreview)
            {
                if (Demenageur.Windows.Security.ApplicationFolderCheck.FindWeakness(AppContext.BaseDirectory) is { } weakness)
                {
                    Log.Warning("{Weakness}", weakness);
                    viewModel.StartupMessages.Add(weakness);
                }

                if (AppHost.DataFolderOutcome == Demenageur.Windows.AppData.SecureDirectoryOutcome.Repaired)
                {
                    const string repaired = "Le dossier de données de Déménageur (C:\\ProgramData\\Demenageur) avait des droits trop larges : "
                        + "ils ont été corrigés. Un autre compte a pu y accéder avant : vérifie la liste de tes déplacements et le journal.";
                    Log.Warning("{Message}", repaired);
                    viewModel.StartupMessages.Add(repaired);
                }

                var explorerAdvice = WindowsMoveHooks.RecoverExplorer(engine.Data.DataRoot, out var explorerDetail);
                if (explorerDetail is not null)
                {
                    Log.Information("Relance de l'Explorateur après une opération interrompue : {Detail}", explorerDetail);
                }

                if (explorerAdvice is not null)
                {
                    Log.Warning("{Advice}", explorerAdvice);
                    viewModel.StartupMessages.Add(explorerAdvice);
                }

                var recovered = await Task.Run(engine.RecoverPending);
                foreach (var (operation, result) in recovered)
                {
                    viewModel.StartupMessages.Add($"{operation.SoftwareName} : {Describe(result)}");
                }

                // Déplacements devenus sans objet (désinstallation, mise à jour qui a recréé le dossier) : signalés, jamais nettoyés d'office.
                try
                {
                    foreach (var moved in await Task.Run(engine.LoadMoves))
                    {
                        if (moved.Health is MoveHealth.SourceMissing or MoveHealth.ReplacedByFolder)
                        {
                            viewModel.StartupMessages.Add($"{moved.SoftwareName} : {moved.HealthMessage} Voir « Mes déplacements » → « Nettoyer la copie… ».");
                        }
                    }
                }
                catch (Exception ex) when (ex is Core.FileSystem.FileSystemException or System.IO.IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Bilan des déplacements impossible au démarrage");
                }
            }

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (previewPage is { } index && index >= 0 && index < viewModel.Pages.Count)
            {
                viewModel.SelectedPage = viewModel.Pages[index];
                PreviewCapture.PrepareOffscreen(window);
                window.Show();
                await PreviewCapture.CaptureAsync(window, capture!.Value.File, captureDelay);
                Shutdown(0);
                return;
            }

            window.Show();
            if (demoRequested)
            {
                _host.Services.GetRequiredService<IWindowService>().ShowDemo();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Échec du démarrage");
            MessageBox.Show($"Déménageur n'a pas pu démarrer :\n\n{ex.Message}", "Déménageur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string Describe(OperationResult result) => result.Outcome switch
    {
        OperationOutcome.Completed => "opération interrompue terminée. " + result.Message,
        OperationOutcome.CompletedWithPendingCleanup => "opération terminée, nettoyage encore en attente. " + result.Message,
        OperationOutcome.RolledBack => "opération interrompue annulée, tout est revenu à l'état initial. " + result.Message,
        _ => "intervention nécessaire. " + result.Message,
    };

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (_host?.Services.GetService<Engine>() is { OperationInProgress: true })
        {
            e.Cancel = true; // arrêt ou déconnexion refusés pendant une opération
        }
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        _instance?.Dispose();
        await Log.CloseAndFlushAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Exception non gérée dans l'interface");
        MessageBox.Show(
            $"Erreur inattendue :\n\n{e.Exception.Message}\n\nLe détail est enregistré dans le journal.",
            "Déménageur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
