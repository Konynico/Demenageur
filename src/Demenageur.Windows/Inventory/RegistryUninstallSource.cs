using System.Text.RegularExpressions;
using Demenageur.Core.Inventory;
using Microsoft.Win32;

namespace Demenageur.Windows.Inventory;

/// <summary>
/// Lecture SEULE des entrées de désinstallation :
/// <list type="bullet">
/// <item>HKLM, vues 64 et 32 bits ;</item>
/// <item>chaque profil chargé sous HKEY_USERS (une app élevée peut tourner sous un autre compte que l'utilisateur
/// connecté, son HKCU ne serait alors pas le bon) ;</item>
/// <item>valeurs lues SANS développement automatique, puis développées selon la vue et le profil de l'entrée.</item>
/// </list>
/// </summary>
public sealed partial class RegistryUninstallSource : IUninstallSource
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    [GeneratedRegex(@"^S-1-(5-21|12-1)-[\d-]+$")]
    private static partial Regex UserSidPattern();

    public IReadOnlyList<UninstallEntry> ReadAll()
    {
        var result = new List<UninstallEntry>();
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            ReadHive(hklm64, @"HKLM\" + UninstallKey, RegistryScope.Machine64, is32: false, null, null, result);
        }

        using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        {
            ReadHive(hklm32, @"HKLM\WOW6432Node\" + UninstallKey, RegistryScope.Machine32, is32: true, null, null, result);
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
            foreach (var sid in users.GetSubKeyNames().Where(s => UserSidPattern().IsMatch(s)))
            {
                using var userRoot = users.OpenSubKey(sid);
                if (userRoot is null)
                {
                    continue;
                }

                ReadHive(userRoot, $@"HKU\{sid}\{UninstallKey}", RegistryScope.User, is32: view == RegistryView.Registry32, sid, ProfileOf(sid), result);
            }
        }

        // Une même clé peut apparaître dans les deux vues d'un profil (HKCU\Software n'est pas redirigé).
        return result.DistinctBy(e => e.RegistryKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadHive(RegistryKey root, string displayPath, RegistryScope scope, bool is32, string? sid, string? profile, List<UninstallEntry> result)
    {
        using var uninstall = root.OpenSubKey(UninstallKey);
        if (uninstall is null)
        {
            return;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            try
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null)
                {
                    continue;
                }

                string? Text(string valueName)
                {
                    var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                    return string.IsNullOrWhiteSpace(raw)
                        ? null
                        : RegistryValueExpander.Expand(raw, is32, profile, Environment.GetEnvironmentVariable);
                }

                var displayName = Text("DisplayName");
                if (displayName is null)
                {
                    continue;
                }

                var releaseType = Text("ReleaseType");
                result.Add(new UninstallEntry
                {
                    RegistryKey = displayPath + @"\" + name,
                    Scope = scope,
                    UserSid = sid,
                    UserProfile = profile,
                    DisplayName = displayName,
                    Publisher = Text("Publisher"),
                    DisplayVersion = Text("DisplayVersion"),
                    InstallLocation = Text("InstallLocation"),
                    DisplayIcon = Text("DisplayIcon"),
                    UninstallString = Text("UninstallString"),
                    EstimatedSizeKb = key.GetValue("EstimatedSize") is int size ? size : null,
                    SystemComponent = key.GetValue("SystemComponent") is int sc && sc != 0,
                    IsUpdate = Text("ParentKeyName") is not null
                               || (releaseType is not null && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))),
                    WindowsInstaller = key.GetValue("WindowsInstaller") is int wi && wi != 0,
                    InstallDate = Text("InstallDate"),
                });
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Clé illisible : ignorée.
            }
        }
    }

    /// <summary>Dossier de profil d'un utilisateur, d'après ProfileList (lecture seule).</summary>
    public static string? ProfileOf(string sid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profile = hklm.OpenSubKey(ProfileListKey + @"\" + sid);
        return profile?.GetValue("ProfileImagePath") is string path ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    /// <summary>SID des utilisateurs dont le profil est chargé (ruche présente sous HKEY_USERS).</summary>
    public static IReadOnlyList<string> LoadedUserSids()
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        return users.GetSubKeyNames().Where(s => UserSidPattern().IsMatch(s)).ToList();
    }

    /// <summary>Profils des utilisateurs actuellement chargés.</summary>
    public static IReadOnlyList<string> LoadedUserProfiles()
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        return users.GetSubKeyNames()
            .Where(s => UserSidPattern().IsMatch(s))
            .Select(ProfileOf)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
