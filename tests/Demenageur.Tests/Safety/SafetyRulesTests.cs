using Demenageur.Core.Inventory;
using Demenageur.Core.Safety;
using Demenageur.Tests.Infrastructure;
using Demenageur.Tests.Inventory;

namespace Demenageur.Tests.Safety;

public sealed class SafetyRulesTests
{
    private static MoveCandidate Candidate(string folder, string name = "Logiciel", string? publisher = null, bool hidden = false, FolderSource source = FolderSource.InstallLocation)
        => new(folder, name, publisher, [new UninstallEntry { RegistryKey = "HKLM\\X\\" + name, Scope = RegistryScope.Machine64, DisplayName = name, Publisher = publisher, SystemComponent = hidden }], source);

    private static IReadOnlyList<RuleVerdict> Evaluate(TestSandbox sandbox, SandboxKnownPaths known, MoveCandidate candidate, SystemSnapshot? system = null, IReadOnlyList<InstalledSoftware>? catalog = null)
        => new SafetyRules(known, sandbox.RawFs).Evaluate(candidate, system ?? new SystemSnapshot(), catalog ?? []);

    private static string App(SandboxKnownPaths known, string name = "App")
    {
        var path = Path.Combine(known.ProgramFiles, name);
        Directory.CreateDirectory(Path.Combine(path, "bin"));
        File.WriteAllText(Path.Combine(path, "bin", "app.exe"), "x");
        return path;
    }

    [Fact]
    public void An_ordinary_program_has_no_blocking_rule()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var verdicts = Evaluate(sandbox, known, Candidate(App(known)));
        Assert.DoesNotContain(verdicts, v => v.Level >= RuleLevel.Confirm);
    }

    [Fact]
    public void System_locations_are_blocked()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        Directory.CreateDirectory(Path.Combine(known.WindowsDirectory, "System32"));
        Directory.CreateDirectory(Path.Combine(known.ProgramFiles, "WindowsApps", "Pkg"));
        Directory.CreateDirectory(Path.Combine(known.ProgramFilesX86, "Microsoft", "Edge"));

        Assert.Contains(Evaluate(sandbox, known, Candidate(Path.Combine(known.WindowsDirectory, "System32"))), v => v.RuleId == "R-WINDOWS" && v.Level == RuleLevel.Block);
        Assert.Contains(Evaluate(sandbox, known, Candidate(known.ProgramFiles)), v => v.RuleId == "R-RACINE" && v.Level == RuleLevel.Block);
        Assert.Contains(Evaluate(sandbox, known, Candidate(known.UserProfiles[0])), v => v.RuleId == "R-RACINE");
        Assert.Contains(Evaluate(sandbox, known, Candidate(Path.Combine(known.ProgramFiles, "WindowsApps", "Pkg"))), v => v.RuleId == "R-COMPOSANT-WINDOWS");
        Assert.Contains(Evaluate(sandbox, known, Candidate(Path.Combine(known.ProgramFilesX86, "Microsoft", "Edge"))), v => v.RuleId == "R-COMPOSANT-WINDOWS");
        Assert.Contains(Evaluate(sandbox, known, Candidate(App(known), "Microsoft Edge WebView2 Runtime")), v => v.RuleId == "R-COMPOSANT-WINDOWS");
        Assert.Contains(Evaluate(sandbox, known, Candidate(known.ApplicationDirectory)), v => v.RuleId == "R-APP");
        Assert.Contains(Evaluate(sandbox, known, Candidate(known.PersonalFolders[0], source: FolderSource.Manual)), v => v.RuleId == "R-PERSO");
    }

    [Fact]
    public void Drivers_security_software_and_system_hooks_are_blocked_even_through_short_names()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var app = App(known, "Logiciel Avec Nom Long");
        var driverPath = Path.Combine(app, "bin", "filtre.sys");
        File.WriteAllText(driverPath, "x");
        var raw = sandbox.RawFs;
        var parent = raw.GetEntry(known.ProgramFiles)!;
        var shortName = raw.ListDirectory(known.ProgramFiles, parent.Identity).Single(c => c.Name == "Logiciel Avec Nom Long").ShortName;
        var referenced = shortName is null ? driverPath : Path.Combine(known.ProgramFiles, shortName, "bin", "filtre.sys");

        var system = new SystemSnapshot
        {
            Services = [new ServiceInfo("filtre", "Filtre", IsDriver: true, 1, @"\??\" + referenced, null)],
            SecurityProducts = [new SecurityProduct("Antivirus Exemple", [Path.Combine(app, "bin")])],
            SystemHooks = [new SystemHook("Paquet LSA", "pkg", Path.Combine(app, "bin", "app.exe"))],
        };
        var verdicts = Evaluate(sandbox, known, Candidate(app), system);

        Assert.Contains(verdicts, v => v.RuleId == "R-PILOTE" && v.Level == RuleLevel.Block);
        Assert.Contains(verdicts, v => v.RuleId == "R-SECURITE" && v.Level == RuleLevel.Block);
        Assert.Contains(verdicts, v => v.RuleId == "R-ACCROCHE" && v.Level == RuleLevel.Block);
        Assert.Contains(Evaluate(sandbox, known, Candidate(App(known, "Av"), "Endpoint", publisher: "ESET, spol. s r.o.")), v => v.RuleId == "R-SECURITE");
    }

    [Fact]
    public void Shared_runtimes_services_and_hidden_components_require_strong_confirmation()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var app = App(known);
        Directory.CreateDirectory(Path.Combine(known.ProgramFiles, "dotnet"));

        Assert.Contains(Evaluate(sandbox, known, Candidate(app, "Microsoft Visual C++ 2015-2022 Redistributable (x64)")), v => v.RuleId == "R-RUNTIME" && v.Level == RuleLevel.Confirm);
        Assert.Contains(Evaluate(sandbox, known, Candidate(Path.Combine(known.ProgramFiles, "dotnet"), "Microsoft .NET Runtime")), v => v.RuleId == "R-RUNTIME" && v.Level == RuleLevel.Confirm);
        Assert.Contains(Evaluate(sandbox, known, Candidate(app, hidden: true)), v => v.RuleId == "R-MASQUE" && v.Level == RuleLevel.Confirm);
        var service = new SystemSnapshot { Services = [new ServiceInfo("monsvc", "Mon service", IsDriver: false, 2, Path.Combine(app, "bin", "app.exe"), null)] };
        var verdicts = Evaluate(sandbox, known, Candidate(app), service);
        Assert.Contains(verdicts, v => v.RuleId == "R-SERVICE" && v.Level == RuleLevel.Confirm);
        Assert.DoesNotContain(verdicts, v => v.Level == RuleLevel.Block);
    }

    [Fact]
    public void Nested_software_pending_renames_and_warnings()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var vendor = Path.Combine(known.ProgramFiles, "Editeur");
        var productA = Path.Combine(vendor, "ProduitA");
        var productB = Path.Combine(vendor, "ProduitB");
        Directory.CreateDirectory(productA);
        Directory.CreateDirectory(productB);
        InstalledSoftware Software(string name, string folder) => new()
        {
            Id = name,
            DisplayName = name,
            Folder = new ResolvedFolder(folder, FolderSource.InstallLocation),
            Entries = [new UninstallEntry { RegistryKey = name, Scope = RegistryScope.Machine64, DisplayName = name }],
        };
        var catalog = new[] { Software("Produit A", productA), Software("Produit B", productB) };

        Assert.Contains(Evaluate(sandbox, known, Candidate(vendor, "Editeur"), catalog: catalog), v => v.RuleId == "R-AUTRES-LOGICIELS" && v.Level == RuleLevel.Block);
        Assert.Contains(Evaluate(sandbox, known, Candidate(productA, "Produit A"), catalog: catalog), v => v.RuleId == "R-VOISINS" && v.Level == RuleLevel.Warning);
        Assert.Contains(Evaluate(sandbox, known, Candidate(productA), new SystemSnapshot { PendingRenames = [Path.Combine(productA, "x.dll")] }), v => v.RuleId == "R-REDEMARRAGE");
        Assert.Contains(Evaluate(sandbox, known, Candidate(productA), new SystemSnapshot { ScheduledTasks = [("MiseAJour", productA + @"\maj.exe /auto")] }), v => v.RuleId == "R-TACHE" && v.Level == RuleLevel.Warning);
        Assert.Contains(Evaluate(sandbox, known, Candidate(productA, source: FolderSource.DisplayIcon)), v => v.RuleId == "R-DEDUIT" && v.Level == RuleLevel.Warning);
    }
}
