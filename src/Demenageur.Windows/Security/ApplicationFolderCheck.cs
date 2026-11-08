using System.Security.AccessControl;
using System.Security.Principal;

namespace Demenageur.Windows.Security;

/// <summary>
/// Auto-contrôle au démarrage : une application lancée en administrateur depuis un dossier modifiable sans élévation
/// (Téléchargements, dossier de projet…) peut être détournée en y déposant une DLL. Recommandé : Program Files.
/// </summary>
public static class ApplicationFolderCheck
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    ];

    /// <summary>Droits qui, sur un dossier parent, permettent de renommer ou supprimer ce qu'il contient (donc de substituer le dossier de l'app).</summary>
    private const FileSystemRights ParentRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// Renvoie un avertissement si le dossier est modifiable par un compte autre qu'administrateur ou SYSTEM, ou si l'un de
    /// ses parents permet à un tel compte de le renommer ou de le remplacer ; sinon null.
    /// </summary>
    public static string? FindWeakness(string directory)
    {
        var current = new DirectoryInfo(directory);
        var rights = WriteRights;
        while (current is not null)
        {
            if (FindUntrustedWriter(current, rights) is { } name)
            {
                var where = string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)
                    ? $"« {directory} », modifiable sans droits administrateur par « {name} »"
                    : $"« {directory} », dont le dossier parent « {current.FullName} » permet à « {name} » de renommer ou remplacer son contenu sans droits administrateur";
                return $"Déménageur est lancé depuis {where}. "
                       + "Pour éviter qu'un programme malveillant n'y dépose une DLL exécutée en administrateur, installe-le dans Program Files (scripts\\installer.ps1).";
            }

            current = current.Parent;
            rights = ParentRights;
        }

        return null;
    }

    private static string? FindUntrustedWriter(DirectoryInfo directory, FileSystemRights dangerous)
    {
        try
        {
            var rules = directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!Trusted.Contains(sid) && (rule.FileSystemRights & dangerous) != 0)
                {
                    try
                    {
                        return sid.Translate(typeof(NTAccount)).Value;
                    }
                    catch (IdentityNotMappedException)
                    {
                        return sid.Value;
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
