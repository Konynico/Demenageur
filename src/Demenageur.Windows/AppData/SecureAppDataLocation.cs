using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.Abstractions;

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
    /// puis crée les sous-dossiers, qui héritent de cette ACL. Nécessite les droits administrateur.
    /// </summary>
    public SecureDirectoryOutcome EnsureSecured()
    {
        var root = new DirectoryInfo(DataRoot);
        SecureDirectoryOutcome outcome;

        if (!root.Exists)
        {
            root.Create(BuildAdminOnlySecurity());
            outcome = SecureDirectoryOutcome.Created;
        }
        else if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Un dossier de données remplacé par une jonction pourrait rediriger nos écritures n'importe où.
            throw new InvalidOperationException(
                $"Le dossier de données « {DataRoot} » est un lien ou une jonction : refus par sécurité.");
        }
        else if (IsSecure(root.GetAccessControl()))
        {
            outcome = SecureDirectoryOutcome.AlreadySecure;
        }
        else
        {
            root.SetAccessControl(BuildAdminOnlySecurity());
            outcome = SecureDirectoryOutcome.Repaired;
        }

        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(OperationsDirectory);
        return outcome;
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
