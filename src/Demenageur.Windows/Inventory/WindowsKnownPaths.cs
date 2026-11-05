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
        PersonalFolders = UserProfiles.SelectMany(PersonalFoldersOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        // Dossiers OneDrive (personnel et professionnel) déclarés pour l'utilisateur courant.
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var accounts = hkcu.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is null)
        {
            yield break;
        }

        foreach (var account in accounts.GetSubKeyNames())
        {
            using var key = accounts.OpenSubKey(account);
            if (key?.GetValue("UserFolder") is string folder && !string.IsNullOrWhiteSpace(folder))
            {
                yield return folder;
            }
        }
    }
}
