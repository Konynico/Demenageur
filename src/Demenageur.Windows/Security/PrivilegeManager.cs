using System.Runtime.InteropServices;
using Demenageur.Windows.Interop;
using static Demenageur.Windows.Interop.NativeMethods;

namespace Demenageur.Windows.Security;

/// <summary>
/// Active, si le jeton les possède (administrateur), les privilèges nécessaires à une copie fidèle :
/// lecture de tous les dossiers (sauvegarde), pose du propriétaire d'origine et des noms courts (restauration),
/// création de liens symboliques. Sans droits administrateur (tests), rien n'est activé.
/// </summary>
public static unsafe class PrivilegeManager
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string CreateSymbolicLink = "SeCreateSymbolicLinkPrivilege";

    /// <summary>Active les privilèges demandés ; renvoie ceux réellement activés.</summary>
    public static IReadOnlyList<string> EnableIfPresent(params string[] privileges)
    {
        var enabled = new List<string>();
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return enabled;
        }

        using (token)
        {
            foreach (var name in privileges)
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    continue;
                }

                var state = new TOKEN_PRIVILEGES_SINGLE { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                // AdjustTokenPrivileges réussit même si le privilège est absent : il faut lire GetLastError.
                if (AdjustTokenPrivileges(token, false, &state, 0, 0, 0) && Marshal.GetLastPInvokeError() == ERROR_SUCCESS)
                {
                    enabled.Add(name);
                }
            }
        }

        return enabled;
    }

    /// <summary>Active les privilèges utiles à Déménageur.</summary>
    public static IReadOnlyList<string> EnableForMoving() => EnableIfPresent(Backup, Restore, CreateSymbolicLink);
}
