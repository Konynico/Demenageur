using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.FileSystem;

public sealed class TreeScannerAndDeleterTests
{
    [Fact]
    public void Scan_sees_everything_and_never_follows_junctions()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("zone", "logiciel");
        var software = new FakeSoftwareBuilder(root) { ExternalJunctionTarget = sandbox.Canary }.Build();
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));

        var snapshot = new TreeScanner(fs).Scan(root, ScanOptions.Full);

        Assert.False(snapshot.HasBlockingProblems, string.Join("\n", snapshot.Problems.Select(p => p.Message)));
        Assert.Equal(software.Files, snapshot.FileCount);
        Assert.Equal(software.Directories, snapshot.DirectoryCount);
        Assert.Equal(software.Links, snapshot.LinkCount);

        // Fichiers cachés et système bien présents (piège des EnumerationOptions par défaut).
        Assert.Contains(snapshot.Entries, e => e.RelativePath == FakeSoftware.HiddenFile);
        Assert.Contains(snapshot.Entries, e => e.RelativePath == FakeSoftware.SystemFile);

        // Le contenu du canari (cible de la jonction externe) n'apparaît pas.
        Assert.DoesNotContain(snapshot.Entries, e => e.RelativePath.Contains("precieux", StringComparison.Ordinal));

        var external = snapshot.Entries.Single(e => e.RelativePath == FakeSoftware.ExternalJunction);
        Assert.Equal(FsEntryKind.Link, external.Kind);
        Assert.Equal(sandbox.Canary, external.Reparse!.Target);

        var ads = snapshot.Entries.Single(e => e.RelativePath == FakeSoftware.AdsFile);
        Assert.Contains(ads.Streams, s => s.Name == FakeSoftware.AdsName);
        Assert.All(snapshot.Entries, e => Assert.NotNull(e.SecurityDescriptor));
        Assert.Contains(snapshot.Problems, p => p.Code == ScanProblemCode.HardLinkInside);
    }

    [Fact]
    public void Hard_link_with_a_name_outside_is_blocking()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("zone", "logiciel");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "partage.dll"), "x");
        TestInterop.CreateHardLink(sandbox.PathOf("zone", "dehors.dll"), Path.Combine(root, "partage.dll"));
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));

        var snapshot = new TreeScanner(fs).Scan(root);
        Assert.Contains(snapshot.Problems, p => p.Code == ScanProblemCode.HardLinkOutside && p.Severity == ScanProblemSeverity.Blocking);
    }

    [Fact]
    public void Root_that_is_a_junction_is_refused()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone", "vrai"));
        TestInterop.MklinkJunction(sandbox.PathOf("zone", "deja-deplace"), sandbox.PathOf("zone", "vrai"));
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));

        var snapshot = new TreeScanner(fs).Scan(sandbox.PathOf("zone", "deja-deplace"));
        Assert.Contains(snapshot.Problems, p => p.Code == ScanProblemCode.RootIsLink);

        var followed = new TreeScanner(fs).Scan(sandbox.PathOf("zone", "deja-deplace"), new ScanOptions { FollowRootLink = true });
        Assert.False(followed.HasBlockingProblems);
    }

    [Fact]
    public void Deleting_a_snapshot_removes_everything_but_never_the_junction_targets()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("zone", "logiciel");
        new FakeSoftwareBuilder(root) { ExternalJunctionTarget = sandbox.Canary }.Build();
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        var snapshot = new TreeScanner(fs).Scan(root);

        var result = new SafeDeleter(fs, RetryPolicy.None).DeleteSnapshot(snapshot);

        Assert.True(result.Complete, string.Join("\n", result.Failures.Select(f => f.Message)));
        Assert.False(Directory.Exists(root));
        Assert.True(sandbox.CanaryIntact());
    }

    [Fact]
    public void Unknown_content_is_never_deleted()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("zone", "logiciel");
        new FakeSoftwareBuilder(root) { IncludeJunctions = false, IncludeLongPath = false }.Build();
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        var snapshot = new TreeScanner(fs).Scan(root);

        File.WriteAllText(Path.Combine(root, "bin", "ajoute-apres-inventaire.txt"), "à garder");
        var result = new SafeDeleter(fs, RetryPolicy.None).DeleteSnapshot(snapshot);

        Assert.False(result.Complete);
        Assert.Contains(result.Failures, f => f.Kind == FsErrorKind.DirectoryNotEmpty);
        Assert.True(File.Exists(Path.Combine(root, "bin", "ajoute-apres-inventaire.txt")));
    }

    [Fact]
    public void A_replaced_file_is_not_deleted()
    {
        using var sandbox = new TestSandbox();
        var root = sandbox.PathOf("zone", "logiciel");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.exe"), "original");
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        var snapshot = new TreeScanner(fs).Scan(root);

        // Substitution : même nom, autre fichier.
        File.Delete(Path.Combine(root, "app.exe"));
        File.WriteAllText(Path.Combine(root, "app.exe"), "remplaçant");

        var result = new SafeDeleter(fs, RetryPolicy.None).DeleteSnapshot(snapshot);
        Assert.Contains(result.Failures, f => f.Kind == FsErrorKind.IdentityMismatch);
        Assert.Equal("remplaçant", File.ReadAllText(Path.Combine(root, "app.exe")));
    }

    [Fact]
    public void DeleteTree_refuses_a_root_with_another_identity()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.PathOf("zone", "a"));
        Directory.CreateDirectory(sandbox.PathOf("zone", "b"));
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        var other = fs.GetEntry(sandbox.PathOf("zone", "b"))!.Identity;

        var ex = Assert.Throws<FileSystemException>(() => new SafeDeleter(fs).DeleteTree(sandbox.PathOf("zone", "a"), other));
        Assert.Equal(FsErrorKind.IdentityMismatch, ex.Kind);
        Assert.True(Directory.Exists(sandbox.PathOf("zone", "a")));
    }
}
