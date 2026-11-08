using System.IO;
using Demenageur.Core.Safety;
using Microsoft.Win32;

namespace Demenageur.Windows.Safety;

/// <summary>
/// Repère les bibliothèques et dossiers de jeux des lanceurs (lecture seule du registre et de leurs fichiers) :
/// Steam (libraryfolders.vdf), Epic (manifestes), GOG Galaxy et Ubisoft Connect (registre), Xbox (.GamingRoot).
/// Battle.net, EA, Riot et Rockstar sont reconnus d'après les entrées de désinstallation (voir LauncherDetection).
/// Chaque source est lue indépendamment : une source illisible n'empêche pas les autres.
/// </summary>
public static class LauncherInspector
{
    public static IReadOnlyList<ManagedLocation> ReadAll()
    {
        var result = new List<ManagedLocation>();
        foreach (var read in new Func<IEnumerable<ManagedLocation>>[] { ReadSteam, ReadEpic, ReadGog, ReadUbisoft, ReadXbox })
        {
            try
            {
                result.AddRange(read());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                // Source illisible : ignorée.
            }
        }

        // Un même dossier peut apparaître plusieurs fois (jeu + extensions GOG) : on garde le titre le plus court, celui du jeu.
        return result
            .OrderBy(l => l.Title?.Length ?? int.MaxValue)
            .DistinctBy(l => (l.LauncherId, l.Path.ToUpperInvariant()))
            .ToList();
    }

    private static IEnumerable<ManagedLocation> ReadSteam()
    {
        var roots = new[]
        {
            // HKLM d'abord : sa valeur garde la casse réelle (SteamPath, dans HKCU, est en minuscules).
            ReadValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
        }.Select(LauncherDetection.NormalizePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var libraries = new List<string> { root };
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                libraries.AddRange(LauncherDetection.ParseSteamLibraryFolders(File.ReadAllText(vdf)));
            }

            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return new ManagedLocation(KnownLaunchers.Steam.Id, Path.Combine(library, "steamapps"));
            }
        }
    }

    private static IEnumerable<ManagedLocation> ReadEpic()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (LauncherDetection.ParseEpicManifest(json) is { } game)
            {
                yield return new ManagedLocation(KnownLaunchers.Epic.Id, game.Path, game.Title);
            }
        }
    }

    private static IEnumerable<ManagedLocation> ReadGog()
        => ReadSubkeyValues(@"SOFTWARE\GOG.com\Games", "path", "gameName", KnownLaunchers.Gog.Id);

    private static IEnumerable<ManagedLocation> ReadUbisoft()
        => ReadSubkeyValues(@"SOFTWARE\Ubisoft\Launcher\Installs", "InstallDir", null, KnownLaunchers.Ubisoft.Id);

    /// <summary>Jeux Xbox : sous-dossiers des racines indiquées par <c>.GamingRoot</c> qui contiennent un jeu (Content\MicrosoftGame.config).</summary>
    private static IEnumerable<ManagedLocation> ReadXbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            if (!File.Exists(marker))
            {
                continue;
            }

            foreach (var gamingRoot in LauncherDetection.ParseGamingRoot(File.ReadAllBytes(marker), drive.RootDirectory.FullName))
            {
                if (!Directory.Exists(gamingRoot))
                {
                    continue;
                }

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(gamingRoot).ToList();
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var game in children)
                {
                    if (File.Exists(Path.Combine(game, "Content", "MicrosoftGame.config")) || File.Exists(Path.Combine(game, "Content", "appxmanifest.xml")))
                    {
                        yield return new ManagedLocation(KnownLaunchers.Xbox.Id, game, Path.GetFileName(game));
                    }
                }
            }
        }
    }

    private static IEnumerable<ManagedLocation> ReadSubkeyValues(string keyPath, string valueName, string? titleName, string launcherId)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (LauncherDetection.NormalizePath(sub?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string) is { } path)
                {
                    yield return new ManagedLocation(launcherId, path, titleName is null ? null : sub!.GetValue(titleName) as string);
                }
            }
        }
    }

    private static string? ReadValue(RegistryHive hive, RegistryView view, string keyPath, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(keyPath);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
