using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.Abstractions;
using Demenageur.Core.FileSystem;
using Demenageur.Windows.FileSystem;

namespace Demenageur.Windows.AppData;

/// <summary>Résultat de la sécurisation du dossier de données.</summary>
public enum SecureDirectoryOutcome
{
    /// <summary>Le dossier n'existait pas : il a été créé directement avec la bonne ACL.</summary>
    Created,

    /// <summary>Le dossier existait déjà avec une ACL conforme.</summary>
    AlreadySecure,

    /// <summary>Le dossier existait avec une ACL trop permissive ou un mauvais propriétaire : il a été réparé.</summary>
    Repaired,
}

/// <summary>
/// Dossier de données de l'application, protégé : seuls SYSTEM et les administrateurs y ont accès.
/// Sans cela, un utilisateur standard pourrait y déposer un faux journal d'opération que l'app,
/// qui tourne en administrateur, exécuterait (rollback vers des chemins arbitraires).
/// </summary>
public sealed class SecureAppDataLocation : IAppDataLocation
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public SecureAppDataLocation()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Demenageur"))
    {
    }

    public SecureAppDataLocation(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string OperationsDirectory => Path.Combine(DataRoot, "operations");

    /// <summary>
    /// Crée le dossier de données (ou le répare) avec une ACL protégée réservée à SYSTEM et aux administrateurs,
    /// contrôle tout ce qu'il contient, puis crée les sous-dossiers. Nécessite les droits administrateur.
    /// <para>
    /// C:\ProgramData laisse tout utilisateur y créer des dossiers : avant le premier lancement, un compte standard peut
    /// préparer « Demenageur » avec des jonctions ou des droits à lui. D'où :
    /// <list type="bullet">
    /// <item>création atomique (CreateDirectoryW avec l'ACL, qui échoue si le dossier est apparu entre-temps) ;</item>
    /// <item>refus de tout lien ou jonction, à n'importe quel niveau ;</item>
    /// <item>refus de tout élément dont le propriétaire n'est ni SYSTEM ni Administrateurs. On ne se l'approprie
    /// JAMAIS : un journal déposé par un autre compte deviendrait sinon « de confiance » ;</item>
    /// <item>retrait des droits accordés à d'autres comptes sur les éléments qui nous appartiennent.</item>
    /// </list>
    /// </para>
    /// </summary>
    public SecureDirectoryOutcome EnsureSecured()
    {
        var outcome = SecureRoot();

        // Le dossier racine est désormais réservé aux administrateurs : son contenu ne peut plus changer sous nos pieds.
        if (SecureContents(DataRoot, AdminSids, AdminSids) && outcome == SecureDirectoryOutcome.AlreadySecure)
        {
            outcome = SecureDirectoryOutcome.Repaired;
        }

        var fs = new Win32FileSystem();
        foreach (var child in new[] { LogsDirectory, OperationsDirectory })
        {
            if (!Directory.Exists(child))
            {
                fs.CreateDirectory(child);
            }
        }

        return outcome;
    }

    private SecureDirectoryOutcome SecureRoot()
    {
        try
        {
            // Pas de fenêtre « existe ? puis crée » : échoue si le dossier existe déjà, même apparu à l'instant.
            new Win32FileSystem().CreateDirectory(DataRoot, BuildAdminOnlySecurity().GetSecurityDescriptorBinaryForm());
            return SecureDirectoryOutcome.Created;
        }
        catch (FileSystemException ex) when (ex.Kind == FsErrorKind.AlreadyExists)
        {
            // Dossier existant : contrôlé ci-dessous.
        }

        var root = new DirectoryInfo(DataRoot);
        RejectReparsePoint(root);
        if (IsSecure(root.GetAccessControl()))
        {
            return SecureDirectoryOutcome.AlreadySecure;
        }

        // Seul le dossier racine est repris (propriétaire Administrateurs) : son contenu est contrôlé, jamais adopté.
        root.SetAccessControl(BuildAdminOnlySecurity());
        return SecureDirectoryOutcome.Repaired;
    }

    private static readonly IReadOnlySet<SecurityIdentifier> AdminSids = new HashSet<SecurityIdentifier> { SystemSid, AdministratorsSid };

    /// <summary>
    /// Parcourt tout le contenu de <paramref name="root"/> sans jamais suivre de lien. Lève une exception sur un lien,
    /// une jonction ou un élément dont le propriétaire n'est pas dans <paramref name="trustedOwners"/>. Retire les droits
    /// accordés à des comptes hors de <paramref name="allowedSids"/> (retour à l'héritage du dossier racine).
    /// Renvoie true si au moins un élément a été corrigé.
    /// </summary>
    public static bool SecureContents(string root, IReadOnlySet<SecurityIdentifier> trustedOwners, IReadOnlySet<SecurityIdentifier> allowedSids)
    {
        var repaired = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                RejectReparsePoint(entry);
                FileSystemSecurity security = entry is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
                var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null || !trustedOwners.Contains(owner))
                {
                    throw new InvalidOperationException(
                        $"« {entry.FullName} » n'appartient pas aux administrateurs (propriétaire : {owner?.Value ?? "inconnu"}). "
                        + "Il a pu être déposé par un autre compte pour détourner Déménageur : refus par sécurité. "
                        + "Vérifie ce qu'il contient ; s'il ne vient pas de Déménageur, supprime-le, puis relance Déménageur.");
                }

                if (GrantsOthers(security, allowedSids))
                {
                    // Retour à l'héritage pur : plus aucun droit explicite, seulement ceux du dossier racine.
                    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
                    {
                        security.RemoveAccessRuleSpecific(rule);
                    }

                    if (entry is DirectoryInfo d)
                    {
                        d.SetAccessControl((DirectorySecurity)security);
                    }
                    else
                    {
                        ((FileInfo)entry).SetAccessControl((FileSecurity)security);
                    }

                    repaired = true;
                }

                if (entry is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory);
                }
            }
        }

        return repaired;
    }

    private static bool GrantsOthers(FileSystemSecurity security, IReadOnlySet<SecurityIdentifier> allowedSids)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !allowedSids.Contains(sid))
            {
                return true;
            }
        }

        return false;
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Une jonction dans le dossier de données pourrait rediriger nos écritures n'importe où.
            throw new InvalidOperationException($"« {info.FullName} » est un lien ou une jonction dans le dossier de données de Déménageur : refus par sécurité.");
        }
    }

    /// <summary>
    /// Mode aperçu (développement de l'interface, sans droits administrateur, simulation uniquement) :
    /// simples dossiers, sans ACL particulière. Jamais utilisé par l'application réelle.
    /// </summary>
    public void EnsurePreviewDirectories()
    {
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(OperationsDirectory);
    }

    /// <summary>
    /// ACL attendue : propriétaire Administrateurs, DACL protégée (pas d'héritage du parent),
    /// contrôle total pour SYSTEM et Administrateurs, aucun autre accès.
    /// </summary>
    public static DirectorySecurity BuildAdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        security.SetOwner(AdministratorsSid);
        return security;
    }

    /// <summary>Vérifie que l'ACL est protégée et n'accorde rien à d'autres que SYSTEM et Administrateurs.</summary>
    public static bool IsSecure(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !(owner.Equals(SystemSid) || owner.Equals(AdministratorsSid)))
        {
            return false;
        }

        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier sid
                && !sid.Equals(SystemSid)
                && !sid.Equals(AdministratorsSid))
            {
                return false;
            }
        }

        return true;
    }
}
