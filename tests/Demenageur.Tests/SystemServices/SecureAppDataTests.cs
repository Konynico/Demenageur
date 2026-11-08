using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;
using Demenageur.Windows.AppData;

namespace Demenageur.Tests.SystemServices;

/// <summary>
/// Contrôle du contenu du dossier de données (audit E2). La sécurisation complète exige les droits administrateur ;
/// ici, le contrôle du contenu est testé sans élévation, en déclarant de confiance l'utilisateur de test
/// (ou au contraire SYSTEM seul, pour simuler des éléments déposés par un autre compte).
/// </summary>
public sealed class SecureAppDataTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private static readonly IReadOnlySet<SecurityIdentifier> Trusted = new HashSet<SecurityIdentifier> { Me, SystemSid, AdministratorsSid };

    private static string CreateData(TestSandbox sandbox)
    {
        var root = sandbox.PathOf("donnees");
        Directory.CreateDirectory(Path.Combine(root, "operations", "20261108-1200-abcd"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(Path.Combine(root, "operations", "20261108-1200-abcd", "journal.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "logs", "demenageur-20261108.log"), "log");
        return root;
    }

    [Fact]
    public void A_clean_data_folder_is_left_untouched()
    {
        using var sandbox = new TestSandbox();
        var root = CreateData(sandbox);

        Assert.False(SecureAppDataLocation.SecureContents(root, Trusted, Trusted));
    }

    [Fact]
    public void Rights_granted_to_other_accounts_are_removed_without_changing_the_owner()
    {
        using var sandbox = new TestSandbox();
        var root = CreateData(sandbox);
        var operations = new DirectoryInfo(Path.Combine(root, "operations"));
        var security = operations.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        operations.SetAccessControl(security);
        var journal = new FileInfo(Path.Combine(root, "operations", "20261108-1200-abcd", "journal.jsonl"));
        Assert.Contains(Rules(journal.GetAccessControl()), r => r.IdentityReference.Equals(Everyone));

        Assert.True(SecureAppDataLocation.SecureContents(root, Trusted, Trusted));

        Assert.DoesNotContain(Rules(operations.GetAccessControl()), r => r.IdentityReference.Equals(Everyone));
        Assert.DoesNotContain(Rules(new FileInfo(journal.FullName).GetAccessControl()), r => r.IdentityReference.Equals(Everyone));
        Assert.Equal(Me, operations.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal("{}", File.ReadAllText(journal.FullName));
        Assert.False(SecureAppDataLocation.SecureContents(root, Trusted, Trusted)); // plus rien à corriger
    }

    [Fact]
    public void A_junction_anywhere_inside_is_refused_and_its_target_untouched()
    {
        using var sandbox = new TestSandbox();
        var root = CreateData(sandbox);
        var target = sandbox.PathOf("chez-un-autre");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a-lui.txt"), "x");
        var fs = sandbox.Guarded(root);
        fs.CreateReparsePoint(Path.Combine(root, "operations", "20261108-1200-abcd", "piege"), isDirectory: true, ReparsePointData.CreateJunction(target));

        var error = Assert.Throws<InvalidOperationException>(() => SecureAppDataLocation.SecureContents(root, Trusted, Trusted));

        Assert.Contains("jonction", error.Message);
        Assert.Equal("x", File.ReadAllText(Path.Combine(target, "a-lui.txt")));
    }

    [Fact]
    public void An_entry_owned_by_another_account_is_refused_never_adopted()
    {
        using var sandbox = new TestSandbox();
        var root = CreateData(sandbox);

        // Seul SYSTEM est de confiance : les éléments (propriété de l'utilisateur de test) jouent le rôle d'éléments
        // déposés par un autre compte.
        var onlySystem = new HashSet<SecurityIdentifier> { SystemSid };
        var error = Assert.Throws<InvalidOperationException>(() => SecureAppDataLocation.SecureContents(root, onlySystem, Trusted));

        Assert.Contains("n'appartient pas aux administrateurs", error.Message);
        Assert.Equal(Me, new DirectoryInfo(Path.Combine(root, "operations")).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    private static IEnumerable<FileSystemAccessRule> Rules(FileSystemSecurity security)
        => security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
}
