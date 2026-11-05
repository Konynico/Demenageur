using Demenageur.Core.Inventory;
using Demenageur.Tests.Infrastructure;
using Demenageur.Windows.Inventory;

namespace Demenageur.Tests.Inventory;

/// <summary>Chemins « système » simulés dans le bac à sable.</summary>
internal sealed class SandboxKnownPaths : IKnownPaths
{
    public SandboxKnownPaths(TestSandbox sandbox)
    {
        var root = sandbox.Root;
        WindowsDirectory = Path.Combine(root, "Windows");
        SystemDrive = root[..2];
        ProgramFiles = Path.Combine(root, "Program Files");
        ProgramFilesX86 = Path.Combine(root, "Program Files (x86)");
        CommonProgramFiles = Path.Combine(ProgramFiles, "Common Files");
        CommonProgramFilesX86 = Path.Combine(ProgramFilesX86, "Common Files");
        ProgramData = Path.Combine(root, "ProgramData");
        UsersDirectory = Path.Combine(root, "Users");
        UserProfiles = [Path.Combine(UsersDirectory, "nico")];
        PersonalFolders = [Path.Combine(UserProfiles[0], "Documents"), Path.Combine(UserProfiles[0], "Desktop")];
        ApplicationDirectory = Path.Combine(ProgramFiles, "Demenageur");
        ApplicationDataDirectory = Path.Combine(ProgramData, "Demenageur");
        foreach (var dir in new[] { WindowsDirectory, ProgramFiles, ProgramFilesX86, ProgramData, UserProfiles[0], ApplicationDirectory })
        {
            Directory.CreateDirectory(dir);
        }
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
}

public sealed class InventoryTests
{
    private static UninstallEntry Entry(string? location = null, string? icon = null, string? uninstall = null)
        => new() { RegistryKey = @"HKLM\X\" + Guid.NewGuid(), Scope = RegistryScope.Machine64, DisplayName = "Logiciel", InstallLocation = location, DisplayIcon = icon, UninstallString = uninstall };

    [Fact]
    public void Expansion_follows_the_registry_view_and_the_user_profile()
    {
        string? Env(string name) => name switch
        {
            "ProgramFiles" => @"C:\Program Files",
            "ProgramFiles(x86)" => @"C:\Program Files (x86)",
            "LOCALAPPDATA" => @"C:\Users\Admin\AppData\Local",
            _ => null,
        };

        Assert.Equal(@"C:\Program Files (x86)\App", RegistryValueExpander.Expand(@"%ProgramFiles%\App", is32BitView: true, null, Env));
        Assert.Equal(@"C:\Program Files\App", RegistryValueExpander.Expand(@"%ProgramFiles%\App", is32BitView: false, null, Env));
        Assert.Equal(@"C:\Users\nico\AppData\Local\Programs\X", RegistryValueExpander.Expand(@"%LOCALAPPDATA%\Programs\X", false, @"C:\Users\nico", Env));
        Assert.Equal(@"C:\Users\Admin\AppData\Local\X", RegistryValueExpander.Expand(@"%LOCALAPPDATA%\X", false, null, Env));
        Assert.Equal(@"%INCONNUE%\X", RegistryValueExpander.Expand(@"%INCONNUE%\X", false, null, Env));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", @"C:\Program Files\App\uninst.exe")]
    [InlineData(@"C:\Program Files\App\app.exe,0", @"C:\Program Files\App\app.exe")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\",-101", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\Mon App\unins000.exe /SILENT", @"C:\Program Files\Mon App\unins000.exe")]
    [InlineData(@"C:\Program Files\App\icone.ico", @"C:\Program Files\App\icone.ico")]
    [InlineData("MsiExec.exe /X{1234}", null)]
    [InlineData("", null)]
    public void Executable_paths_are_extracted_from_commands(string value, string? expected)
        => Assert.Equal(expected, InstallFolderResolver.ExtractExecutablePath(value));

    [Fact]
    public void Folder_is_resolved_from_install_location_or_deduced()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var product = Path.Combine(known.ProgramFiles, "Editeur", "Produit");
        Directory.CreateDirectory(Path.Combine(product, "bin"));
        File.WriteAllText(Path.Combine(product, "bin", "app.exe"), "x");
        var cache = Path.Combine(known.ProgramData, "Package Cache", "{GUID}");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "setup.exe"), "x");
        var resolver = new InstallFolderResolver(sandbox.RawFs, known);

        Assert.Equal(new ResolvedFolder(product, FolderSource.InstallLocation), resolver.Resolve(Entry(location: "\"" + product + "\\\"")));

        // InstallLocation absent ou trop large : déduction depuis l'icône, en remontant « bin ».
        var fromIcon = resolver.Resolve(Entry(location: known.ProgramFiles, icon: Path.Combine(product, "bin", "app.exe") + ",0"));
        Assert.Equal(product, fromIcon.Path);
        Assert.True(fromIcon.NeedsConfirmation);

        Assert.Equal(product, resolver.Resolve(Entry(uninstall: "\"" + Path.Combine(product, "bin", "app.exe") + "\" /uninstall")).Path);
        Assert.Null(resolver.Resolve(Entry(uninstall: "MsiExec.exe /X{1234}")).Path);
        Assert.Null(resolver.Resolve(Entry(uninstall: Path.Combine(cache, "setup.exe") + " /uninstall")).Path);
        Assert.Null(resolver.Resolve(Entry(location: Path.Combine(known.ProgramFiles, "absent"))).Path);
    }

    [Fact]
    public void Folder_size_counts_everything_and_does_not_follow_junctions()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("logiciel");
        var software = new FakeSoftwareBuilder(root) { ExternalJunctionTarget = sandbox.Canary }.Build();

        var size = new FolderSizeCalculator(sandbox.Guarded(sandbox.PathOf("logiciel"))).Compute(root);

        var expected = Directory.EnumerateFiles(@"\\?\" + root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Sum(f => new FileInfo(f).Length);
        Assert.Equal(expected, size.Bytes);
        Assert.Equal(software.Files, size.Files);
        Assert.False(size.Partial);
    }

    [Fact]
    public void Catalog_groups_duplicate_entries_and_drops_updates()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var product = Path.Combine(known.ProgramFiles, "App");
        Directory.CreateDirectory(product);
        var entries = new[]
        {
            Entry(location: product) with { DisplayName = "App", Scope = RegistryScope.Machine64 },
            Entry(location: product) with { DisplayName = "App ", Scope = RegistryScope.Machine32, SystemComponent = true },
            Entry(location: product) with { DisplayName = "Correctif KB123 pour App", IsUpdate = true },
            Entry() with { DisplayName = "Sans dossier" },
        };
        var catalog = new SoftwareCatalog(new StubSource(entries), new InstallFolderResolver(sandbox.RawFs, known)).Load();

        Assert.Equal(2, catalog.Count);
        var app = catalog.Single(s => s.DisplayName == "App");
        Assert.Equal(2, app.Entries.Count);
        Assert.False(app.IsHidden);
        Assert.Equal(FolderSource.None, catalog.Single(s => s.DisplayName == "Sans dossier").Folder.Source);
    }

    [Fact]
    public void Real_registry_can_be_read_without_writing()
    {
        // Lecture seule du vrai registre (aucune modification possible : écritures interdites à la compilation).
        var entries = new RegistryUninstallSource().ReadAll();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.DisplayName)));
        Assert.Contains(entries, e => e.Scope == RegistryScope.Machine64);
    }

    private sealed class StubSource(IReadOnlyList<UninstallEntry> entries) : IUninstallSource
    {
        public IReadOnlyList<UninstallEntry> ReadAll() => entries;
    }
}
