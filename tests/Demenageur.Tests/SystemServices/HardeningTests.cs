using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;
using Demenageur.Windows.AppData;
using Demenageur.Windows.Security;

namespace Demenageur.Tests.SystemServices;

/// <summary>Durcissements de l'audit (F2, F9, F12), sur dossiers factices du bac à sable.</summary>
public sealed class HardeningTests
{
    [Fact]
    public void The_demo_refuses_to_write_through_a_junction_on_its_path()
    {
        using var sandbox = new TestSandbox();
        var drive = sandbox.PathOf("disque");
        Directory.CreateDirectory(drive);
        var elsewhere = sandbox.PathOf("chez-un-autre");
        Directory.CreateDirectory(elsewhere);
        sandbox.Guarded(drive).CreateReparsePoint(Path.Combine(drive, DemoSoftwareFactory.DemoFolderName), isDirectory: true, ReparsePointData.CreateJunction(elsewhere));

        var error = Assert.Throws<InvalidOperationException>(() => DemoSoftwareFactory.Create(drive));

        Assert.Contains("jonction", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void The_demo_is_created_normally_when_the_path_is_clean()
    {
        using var sandbox = new TestSandbox();
        var drive = sandbox.PathOf("disque");
        Directory.CreateDirectory(drive);

        var folder = DemoSoftwareFactory.Create(drive);

        Assert.True(File.Exists(Path.Combine(folder, "config.ini")));
    }

    [Fact]
    public void A_folder_writable_by_the_user_is_reported_but_program_files_is_not()
    {
        using var sandbox = new TestSandbox();
        var mine = sandbox.PathOf("app");
        Directory.CreateDirectory(mine);

        Assert.NotNull(ApplicationFolderCheck.FindWeakness(mine)); // %TEMP% : modifiable par l'utilisateur de test
        Assert.Null(ApplicationFolderCheck.FindWeakness(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.Null(ApplicationFolderCheck.FindWeakness(Environment.SystemDirectory));
    }

    [Fact]
    public void The_readme_is_not_readable_by_other_users()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("Logiciels déplacés");
        Directory.CreateDirectory(root);

        ReadmeWriter.Update(root, []);

        var security = new FileInfo(Path.Combine(root, ReadmeWriter.FileName)).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(users) || r.IdentityReference.Equals(authenticated));
        Assert.Contains(rules, r => r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User));
        Assert.Contains("Déménageur", File.ReadAllText(Path.Combine(root, ReadmeWriter.FileName)), StringComparison.Ordinal);

        ReadmeWriter.Update(root, []); // mise à jour d'un fichier existant : toujours possible
    }
}
