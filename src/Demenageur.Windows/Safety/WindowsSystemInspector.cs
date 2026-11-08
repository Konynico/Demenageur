using System.Management;
using System.Text.RegularExpressions;
using Demenageur.Core.Inventory;
using Demenageur.Core.Safety;
using Microsoft.Win32;

namespace Demenageur.Windows.Safety;

/// <summary>
/// Lecture SEULE de l'état système utile aux règles : services et pilotes, logiciels de sécurité (Centre de sécurité),
/// points d'accroche système, serveurs COM, tâches planifiées, renommages en attente.
/// Chaque lecture échoue silencieusement (liste vide) plutôt que d'empêcher l'analyse.
/// </summary>
public sealed partial class WindowsSystemInspector : ISystemInspector
{
    private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [GeneratedRegex(@"<(Command|Arguments|WorkingDirectory)>(.*?)</\1>", RegexOptions.Singleline)]
    private static partial Regex TaskPathPattern();

    public SystemSnapshot Capture() => new()
    {
        Services = Safe(ReadServices),
        SecurityProducts = Safe(ReadSecurityProducts),
        SystemHooks = Safe(ReadSystemHooks),
        ComServers = Safe(ReadComServers),
        ScheduledTasks = Safe(ReadScheduledTasks),
        PendingRenames = Safe(ReadPendingRenames),
        LauncherLocations = Safe(LauncherInspector.ReadAll),
    };

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ManagementException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Normalise un chemin de service ou de pilote (\SystemRoot\, system32\, \??\, guillemets, arguments).</summary>
    public string? NormalizeImagePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            value = _windows + value[11..];
        }
        else if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[4..];
        }
        else if (value.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase) || value.StartsWith(@"syswow64\", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(_windows, value);
        }

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : value.Trim('"');
        }

        foreach (var extension in new[] { ".exe", ".sys", ".dll" })
        {
            var index = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return value[..(index + extension.Length)];
            }
        }

        return value;
    }

    private IReadOnlyList<ServiceInfo> ReadServices()
    {
        var result = new List<ServiceInfo>();
        using var hklm = Hklm();
        using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return result;
        }

        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                AddService(services, name, result);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Clé de service illisible (sans droits administrateur) : ignorée.
            }
        }

        return result;
    }

    private void AddService(RegistryKey services, string name, List<ServiceInfo> result)
    {
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("Type") is not int type)
            {
                return;
            }

            using var parameters = key.OpenSubKey("Parameters");
            var displayName = key.GetValue("DisplayName") as string ?? name;
            if (displayName.StartsWith('@'))
            {
                displayName = name;
            }

            result.Add(new ServiceInfo(
                name,
                displayName,
                IsDriver: (type & 0x3) != 0,
                StartType: key.GetValue("Start") is int start ? start : 3,
                ImagePath: NormalizeImagePath(key.GetValue("ImagePath") as string),
                ServiceDll: NormalizeImagePath(parameters?.GetValue("ServiceDll") as string ?? key.GetValue("ServiceDll") as string)));
        }
    }

    private static IReadOnlyList<SecurityProduct> ReadSecurityProducts()
    {
        var result = new List<SecurityProduct>();
        foreach (var className in new[] { "AntiVirusProduct", "AntiSpywareProduct", "FirewallProduct" })
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", $"SELECT displayName, pathToSignedProductExe, pathToSignedReportingExe FROM {className}");
            foreach (ManagementObject product in searcher.Get())
            {
                using (product)
                {
                    var paths = new[] { product["pathToSignedProductExe"] as string, product["pathToSignedReportingExe"] as string }
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => InstallFolderResolver.ExtractExecutablePath(Environment.ExpandEnvironmentVariables(p!)))
                        .OfType<string>()
                        .Select(p => Path.GetDirectoryName(p)!)
                        .ToList();
                    result.Add(new SecurityProduct(product["displayName"] as string ?? className, paths));
                }
            }
        }

        return result;
    }

    private IReadOnlyList<SystemHook> ReadSystemHooks()
    {
        var hooks = new List<SystemHook>();
        var system32 = Path.Combine(_windows, "System32");
        using var hklm = Hklm();

        foreach (var (keyPath, kind) in new[]
                 {
                     (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers", "Fournisseur d'identification"),
                     (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters", "Filtre d'identification"),
                 })
        {
            using var providers = hklm.OpenSubKey(keyPath);
            foreach (var clsid in providers?.GetSubKeyNames() ?? [])
            {
                if (ComServerPath(hklm, clsid) is { } path)
                {
                    hooks.Add(new SystemHook(kind, clsid, path));
                }
            }
        }

        using (var lsa = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Lsa"))
        {
            foreach (var valueName in new[] { "Authentication Packages", "Security Packages", "Notification Packages" })
            {
                foreach (var package in lsa?.GetValue(valueName) as string[] ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(package))
                    {
                        hooks.Add(new SystemHook("Paquet LSA", package, Path.IsPathRooted(package) ? package : Path.Combine(system32, package.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? package : package + ".dll")));
                    }
                }
            }
        }

        using (var monitors = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Monitors"))
        {
            foreach (var monitor in monitors?.GetSubKeyNames() ?? [])
            {
                using var key = monitors!.OpenSubKey(monitor);
                if (key?.GetValue("Driver") is string driver)
                {
                    hooks.Add(new SystemHook("Moniteur d'impression", monitor, Path.IsPathRooted(driver) ? driver : Path.Combine(system32, driver)));
                }
            }
        }

        using (var winlogon = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            foreach (var valueName in new[] { "Userinit", "Shell", "GinaDLL", "Taskman" })
            {
                foreach (var part in (winlogon?.GetValue(valueName) as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (NormalizeImagePath(part) is { } path && Path.IsPathRooted(path))
                    {
                        hooks.Add(new SystemHook("Ouverture de session (Winlogon)", valueName, path));
                    }
                }
            }
        }

        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion" })
        {
            using var ifeo = hklm.OpenSubKey(root + @"\Image File Execution Options");
            foreach (var image in ifeo?.GetSubKeyNames() ?? [])
            {
                using var key = ifeo!.OpenSubKey(image);
                if (NormalizeImagePath(key?.GetValue("Debugger") as string) is { } debugger && Path.IsPathRooted(debugger))
                {
                    hooks.Add(new SystemHook("Débogueur imposé (IFEO)", image, debugger));
                }
            }

            using var windows = hklm.OpenSubKey(root + @"\Windows");
            foreach (var dll in (windows?.GetValue("AppInit_DLLs") as string ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                hooks.Add(new SystemHook("AppInit_DLLs", dll, dll));
            }
        }

        return hooks;
    }

    private static string? ComServerPath(RegistryKey hklm, string clsid)
    {
        foreach (var classes in new[] { @"SOFTWARE\Classes\CLSID", @"SOFTWARE\WOW6432Node\Classes\CLSID" })
        {
            using var server = hklm.OpenSubKey($@"{classes}\{clsid}\InprocServer32") ?? hklm.OpenSubKey($@"{classes}\{clsid}\LocalServer32");
            if (server?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
            {
                return InstallFolderResolver.ExtractExecutablePath(Environment.ExpandEnvironmentVariables(path)) ?? Environment.ExpandEnvironmentVariables(path).Trim('"');
            }
        }

        return null;
    }

    private static IReadOnlyList<SystemHook> ReadComServers()
    {
        var result = new List<SystemHook>();
        using var hklm = Hklm();
        foreach (var classes in new[] { @"SOFTWARE\Classes\CLSID", @"SOFTWARE\WOW6432Node\Classes\CLSID" })
        {
            using var clsids = hklm.OpenSubKey(classes);
            foreach (var clsid in clsids?.GetSubKeyNames() ?? [])
            {
                foreach (var serverKey in new[] { "InprocServer32", "LocalServer32" })
                {
                    try
                    {
                        using var server = clsids!.OpenSubKey(clsid + @"\" + serverKey);
                        if (server?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                        {
                            var expanded = Environment.ExpandEnvironmentVariables(path);
                            result.Add(new SystemHook("COM", clsid, InstallFolderResolver.ExtractExecutablePath(expanded) ?? expanded.Trim('"')));
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // Clé illisible : ignorée.
                    }
                }
            }
        }

        return result;
    }

    private IReadOnlyList<(string, string)> ReadScheduledTasks()
    {
        var root = Path.Combine(_windows, "System32", "Tasks");
        var result = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(root, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                var xml = File.ReadAllText(file);
                var paths = string.Join(" ", TaskPathPattern().Matches(xml).Select(m => Environment.ExpandEnvironmentVariables(m.Groups[2].Value)));
                if (paths.Length > 0)
                {
                    result.Add((Path.GetRelativePath(root, file), paths));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tâche illisible : ignorée.
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ReadPendingRenames()
    {
        using var hklm = Hklm();
        using var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return (session?.GetValue("PendingFileRenameOperations") as string[] ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimStart('!'))
            .Select(p => p.StartsWith(@"\??\", StringComparison.Ordinal) ? p[4..] : p)
            .ToList();
    }
}
