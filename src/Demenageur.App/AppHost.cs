using Demenageur.Core.Abstractions;
using Demenageur.Windows.AppData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Demenageur.App;

/// <summary>Construction de l'hôte (injection de dépendances + logs).</summary>
internal static class AppHost
{
    public static IHost Build(string[] args)
    {
        // 1) Dossier de données protégé AVANT toute écriture (logs compris).
        var dataLocation = new SecureAppDataLocation();
        var outcome = dataLocation.EnsureSecured();

        // 2) Logs techniques.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(dataLocation.LogsDirectory, "demenageur-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Démarrage de Déménageur (dossier de données : {Outcome})", outcome);

        // 3) Hôte sans configuration implicite (pas d'appsettings ni de variables d'environnement lues).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            DisableDefaults = true,
        });

        builder.Services.AddSerilog(Log.Logger, dispose: false);
        builder.Services.AddSingleton<IAppDataLocation>(dataLocation);
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
