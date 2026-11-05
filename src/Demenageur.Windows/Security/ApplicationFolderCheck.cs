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

    /// <summary>Renvoie un avertissement si le dossier est modifiable par un compte autre qu'administrateur ou SYSTEM ; sinon null.</summary>
    public static string? FindWeakness(string directory)
    {
        try
        {
            var rules = new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!Trusted.Contains(sid) && (rule.FileSystemRights & WriteRights) != 0)
                {
                    string name;
                    try
                    {
                        name = sid.Translate(typeof(NTAccount)).Value;
                    }
                    catch (IdentityNotMappedException)
                    {
                        name = sid.Value;
                    }

                    return $"Déménageur est lancé depuis « {directory} », modifiable sans droits administrateur par « {name} ». "
                           + "Pour éviter qu'un programme malveillant n'y dépose une DLL exécutée en administrateur, installe-le dans Program Files (scripts\\installer.ps1).";
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
