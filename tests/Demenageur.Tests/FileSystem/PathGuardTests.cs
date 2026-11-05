using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.FileSystem;

public sealed class PathGuardTests
{
    [Theory]
    [InlineData(@"..\x")]
    [InlineData(@"relatif\x")]
    [InlineData(@"C:dossier")]
    [InlineData(@"\\serveur\partage\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    [InlineData(@"\\.\C:")]
    [InlineData(@"\\?\UNC\serveur\partage")]
    public void Rejects_malformed_or_device_paths(string path)
    {
        using var sandbox = new TestSandbox();
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));
        Assert.Throws<PathGuardViolationException>(() => guard.CreateDirectory(path));
    }

    [Fact]
    public void Rejects_dot_dot_segments_and_stream_syntax()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));

        Assert.Throws<PathGuardViolationException>(() => guard.CreateDirectory(sandbox.PathOf("zone") + @"\..\_canari\x"));
        Assert.Throws<PathGuardViolationException>(() => guard.WriteNewFile(sandbox.PathOf("zone", "f.txt") + "::$DATA", [1]));
        Assert.Throws<PathGuardViolationException>(() => guard.WriteNewFile(sandbox.PathOf("zone", "f.txt") + ":ads", [1]));
    }

    [Fact]
    public void Allows_writes_inside_writable_root_only()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        Directory.CreateDirectory(sandbox.PathOf("ailleurs"));
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));

        guard.CreateDirectory(sandbox.PathOf("zone", "nouveau"));
        Assert.True(Directory.Exists(sandbox.PathOf("zone", "nouveau")));

        Assert.Throws<PathGuardViolationException>(() => guard.CreateDirectory(sandbox.PathOf("ailleurs", "x")));
        Assert.False(Directory.Exists(sandbox.PathOf("ailleurs", "x")));
    }

    [Fact]
    public void Rejects_writes_into_the_canary_even_through_a_junction_alias()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        TestInterop.MklinkJunction(sandbox.PathOf("zone", "alias"), sandbox.Canary);
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));

        // Le chemin « zone\alias\x » est textuellement dans la zone, mais il atterrit en réalité dans le canari.
        Assert.Throws<PathGuardViolationException>(() => guard.CreateDirectory(sandbox.PathOf("zone", "alias", "x")));
        Assert.Throws<PathGuardViolationException>(() => guard.WriteNewFile(sandbox.PathOf("zone", "alias", "precieux2.txt"), [1, 2, 3]));
        Assert.True(sandbox.CanaryIntact());
    }

    [Fact]
    public void Deleting_a_junction_itself_is_allowed_and_leaves_its_target_intact()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        TestInterop.MklinkJunction(sandbox.PathOf("zone", "alias"), sandbox.Canary);
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));

        var link = guard.GetEntry(sandbox.PathOf("zone", "alias"))!;
        guard.Delete(sandbox.PathOf("zone", "alias"), link.Identity);

        Assert.False(Directory.Exists(sandbox.PathOf("zone", "alias")));
        Assert.True(sandbox.CanaryIntact());
    }

    [Fact]
    public void Rejects_reads_outside_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));
        Assert.Throws<PathGuardViolationException>(() => guard.GetEntry(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.Throws<PathGuardViolationException>(() => guard.GetEntry(@"C:\Program Files"));
    }

    [Fact]
    public void Rejects_writing_to_a_protected_exact_path()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        var guard = sandbox.Guarded(sandbox.PathOf("zone"));
        var root = guard.GetEntry(sandbox.PathOf("zone"))!;
        // Renommer la zone revient à écrire dans la racine du bac à sable (protégée).
        Assert.Throws<PathGuardViolationException>(() => guard.Rename(sandbox.PathOf("zone"), root.Identity, "autre"));
    }

    [Fact]
    public void Scope_creation_rejects_dangerous_writable_roots()
    {
        using var sandbox = new TestSandbox();
        Assert.Throws<PathGuardViolationException>(() => PathScope.Create(sandbox.RawFs, [@"C:\"]));
        Assert.Throws<PathGuardViolationException>(() => PathScope.Create(sandbox.RawFs, [sandbox.Root], protectedSubtrees: [sandbox.Canary]));
        Assert.Throws<PathGuardViolationException>(() => PathScope.Create(sandbox.RawFs, [sandbox.PathOf("_canari", "x")], protectedSubtrees: [sandbox.Canary]));
        Assert.Throws<PathGuardViolationException>(() => PathScope.Create(sandbox.RawFs, [@"C:\Ailleurs"], sandboxRoot: sandbox.Root));
    }

    [Fact]
    public void Short_name_alias_is_resolved_before_checking()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        Directory.CreateDirectory(sandbox.PathOf("dossier au nom tres long"));
        var raw = sandbox.RawFs;
        var rootInfo = raw.GetEntry(sandbox.Root)!;
        var child = raw.ListDirectory(sandbox.Root, rootInfo.Identity).Single(c => c.Name == "dossier au nom tres long");
        Assert.SkipWhen(child.ShortName is null, "Noms courts 8.3 désactivés sur ce volume.");

        var guard = sandbox.Guarded(sandbox.PathOf("zone"));
        // L'alias court désigne un dossier hors de la zone : refus.
        Assert.Throws<PathGuardViolationException>(() => guard.CreateDirectory(Path.Combine(sandbox.Root, child.ShortName!, "x")));
    }
}
