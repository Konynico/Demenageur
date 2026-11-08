using System.IO;
using Demenageur.App.Services;
using Demenageur.App.ViewModels;
using Demenageur.Core.Abstractions;
using Demenageur.Windows.AppData;
using Demenageur.Windows.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Demenageur.App;

/// <summary>Construction de l'hôte (injection de dépendances + logs).</summary>
internal static class AppHost
{
#if APERCU
    public static readonly bool IsPreview = true;
#else
    public static readonly bool IsPreview = false;
#endif

    /// <summary>Résultat de la sécurisation du dossier de données (null en aperçu).</summary>
    public static SecureDirectoryOutcome? DataFolderOutcome { get; private set; }

    public static IHost Build(string[] args)
    {
        // 1) Dossier de données protégé AVANT toute écriture (logs compris).
        SecureAppDataLocation dataLocation;
        string outcome;
        if (IsPreview)
        {
            dataLocation = new SecureAppDataLocation(Path.Combine(Path.GetTempPath(), "Demenageur-Apercu"));
            dataLocation.EnsurePreviewDirectories();
            outcome = "aperçu";
        }
        else
        {
            dataLocation = new SecureAppDataLocation();
            DataFolderOutcome = dataLocation.EnsureSecured();
            outcome = DataFolderOutcome.ToString()!;
        }

        // 2) Logs techniques.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(dataLocation.LogsDirectory, "demenageur-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 3) Privilèges de sauvegarde/restauration (lecture de tout, conservation des propriétaires).
        var privileges = IsPreview ? [] : PrivilegeManager.EnableForMoving();
        Log.Information("Démarrage de Déménageur (données : {Outcome}, privilèges : {Privileges})", outcome, string.Join(", ", privileges));

        // 4) Hôte sans configuration implicite (pas d'appsettings ni de variables d'environnement lues).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            DisableDefaults = true,
        });

        builder.Services.AddSerilog(Log.Logger, dispose: false);
        builder.Services.AddSingleton<IAppDataLocation>(dataLocation);
        builder.Services.AddSingleton(sp => new Engine(sp.GetRequiredService<IAppDataLocation>(), IsPreview));
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<SoftwareListViewModel>();
        builder.Services.AddSingleton<MovesViewModel>();
        builder.Services.AddSingleton<JournalViewModel>();
        builder.Services.AddSingleton<HelpViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
