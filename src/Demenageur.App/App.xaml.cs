using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Demenageur.App;

public partial class App : Application
{
    private IHost? _host;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (!IsElevated())
        {
            // Le manifeste exige l'élévation ; on ne fait rien sans elle, par sécurité.
            MessageBox.Show(
                "Déménageur doit être lancé en tant qu'administrateur.",
                "Déménageur",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            _host = AppHost.Build(e.Args);
            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Échec du démarrage");
            MessageBox.Show(
                $"Déménageur n'a pas pu démarrer :\n\n{ex.Message}",
                "Déménageur",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

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
