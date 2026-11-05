using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Moving;

namespace Demenageur.Core.Safety;

/// <summary>
/// Dossier racine qui accueille les logiciels déplacés (ex. <c>D:\Logiciels déplacés</c>).
/// Créé par Déménageur avec une ACL protégée : SYSTEM, Administrateurs et l'utilisateur administrateur en contrôle total,
/// Utilisateurs en lecture/exécution. S'il existe déjà avec des droits plus larges (un non-administrateur pourrait
/// y remplacer un dossier de logiciel) ou un propriétaire inconnu, il est refusé.
/// </summary>
public static class DestinationContainer
{
    public const string DefaultFolderName = "Logiciels déplacés";

    private const FileSystemRights DangerousRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    private const int GenericWrite = 0x40000000;
    private const int GenericAll = 0x10000000;

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static byte[] CreateDescriptor()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid, identity.User! }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security.GetSecurityDescriptorBinaryForm();
    }

    /// <summary>Vérifie un dossier racine existant. Null si correct ou absent (il sera créé).</summary>
    public static RuleVerdict? Check(IFileSystem fs, string container)
    {
        var entry = fs.GetEntry(container);
        if (entry is null)
        {
            return null;
        }

        if (entry.Kind != FsEntryKind.Directory)
        {
            return new RuleVerdict("R-DEST-RACINE", RuleLevel.Block, "Dossier de destination invalide", $"« {container} » n'est pas un dossier ordinaire (lien ou fichier).");
        }

        var sd = fs.GetSecurityDescriptor(container, entry.Identity);
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid, TrustedInstallerSid, identity.User! };
        var owner = SecurityDescriptorTools.GetOwner(sd);
        if (owner is null || !trusted.Contains(owner))
        {
            return new RuleVerdict("R-DEST-RACINE", RuleLevel.Block, "Dossier de destination non sûr",
                $"« {container} » existe déjà et appartient à {owner?.Value ?? "un compte inconnu"} : un autre utilisateur pourrait y remplacer les fichiers d'un logiciel. Choisis un autre dossier.");
        }

        var raw = new RawSecurityDescriptor(sd, 0);
        if (raw.DiscretionaryAcl is { } dacl)
        {
            foreach (var ace in dacl.OfType<CommonAce>())
            {
                if (ace.AceType != AceType.AccessAllowed || (ace.AceFlags & AceFlags.InheritOnly) != 0 || trusted.Contains(ace.SecurityIdentifier))
                {
                    continue;
                }

                var rights = (FileSystemRights)ace.AccessMask;
                if ((rights & DangerousRights) != 0 || (ace.AccessMask & (GenericWrite | GenericAll)) != 0)
                {
                    return new RuleVerdict("R-DEST-RACINE", RuleLevel.Block, "Dossier de destination non sûr",
                        $"« {container} » existe déjà et permet à {ace.SecurityIdentifier.Value} de modifier son contenu : un non-administrateur pourrait remplacer les fichiers d'un logiciel déplacé (élévation de privilèges). Choisis un autre dossier ou supprime-le s'il est vide.");
                }
            }
        }

        return null;
    }

    /// <summary>Crée le dossier racine s'il n'existe pas (garde limitée à ce seul chemin).</summary>
    public static void EnsureCreated(IFileSystem raw, IPathScopeFactory scopes, string container)
    {
        if (raw.GetEntry(container) is not null)
        {
            return;
        }

        var guard = new PathGuard(raw, scopes.Create(raw, [container]));
        guard.CreateDirectory(container, CreateDescriptor());
    }
}
