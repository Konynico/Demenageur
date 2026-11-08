using Demenageur.Core.Inventory;
using Microsoft.Win32;

namespace Demenageur.Windows.Inventory;

/// <summary>Chemins système réels de cette machine.</summary>
public sealed class WindowsKnownPaths : IKnownPaths
{
    public WindowsKnownPaths(string applicationDirectory, string applicationDataDirectory)
    {
        ApplicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        ApplicationDataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataDirectory));
        WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        SystemDrive = Path.GetPathRoot(WindowsDirectory)!.TrimEnd('\\');
        ProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        CommonProgramFiles = Environment.GetEnvironmentVariable("CommonProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        CommonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        UsersDirectory = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? SystemDrive + @"\Users";
        UserProfiles = RegistryUninstallSource.LoadedUserProfiles();
        PersonalFolders = UserProfiles.SelectMany(PersonalFoldersOf).Concat(OneDriveFolders()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string WindowsDirectory { get; }

    public string SystemDrive { get; }

    public string ProgramFiles { get; }

    public string ProgramFilesX86 { get; }

    public string CommonProgramFiles { get; }

    public string CommonProgramFilesX86 { get; }

    public string ProgramData { get; }

    public string UsersDirectory { get; }

    public IReadOnlyList<string> UserProfiles { get; }

    public IReadOnlyList<string> PersonalFolders { get; }

    public string ApplicationDirectory { get; }

    public string ApplicationDataDirectory { get; }

    private static IEnumerable<string> PersonalFoldersOf(string profile)
    {
        foreach (var name in new[] { "Desktop", "Bureau", "Documents", "Downloads", "Music", "Pictures", "Videos", "OneDrive", "Favorites", "Contacts", "Saved Games", "Searches", "Links" })
        {
            yield return Path.Combine(profile, name);
        }

    }

    /// <summary>
    /// Dossiers OneDrive (personnel et professionnel) de CHAQUE profil chargé, lus dans HKEY_USERS\&lt;SID&gt; :
    /// en administrateur, HKCU ne désigne que le compte qui a validé l'UAC, pas les autres utilisateurs.
    /// </summary>
    private static List<string> OneDriveFolders()
    {
        var folders = new List<string>();
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        var hives = new List<(RegistryKey Root, string Prefix)> { (hkcu, string.Empty) };
        hives.AddRange(RegistryUninstallSource.LoadedUserSids().Select(sid => (users, sid + @"\")));
        foreach (var (root, prefix) in hives)
        {
            try
            {
                using var accounts = root.OpenSubKey(prefix + @"Software\Microsoft\OneDrive\Accounts");
                foreach (var account in accounts?.GetSubKeyNames() ?? [])
                {
                    using var key = accounts!.OpenSubKey(account);
                    if (key?.GetValue("UserFolder") is string folder && !string.IsNullOrWhiteSpace(folder))
                    {
                        folders.Add(folder);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Ruche illisible : ignorée (les dossiers standard du profil restent protégés).
            }
        }

        return folders;
    }
}
