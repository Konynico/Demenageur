using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.Copy;

public sealed class CopyAndVerifyTests
{
    [Fact]
    public void Copy_is_faithful_and_passes_full_verification()
    {
        using var scenario = new CopyScenario();
        scenario.CopyAll();

        var result = scenario.Verify();
        Assert.True(result.Success, result.Summary);
        Assert.True(result.BytesHashed > scenario.Manifest!.TotalBytes / 2);

        // Contrôles directs, indépendants du vérificateur.
        Assert.Equal(TestSandbox.HashTreeContentOnly(scenario.Source), TestSandbox.HashTreeContentOnly(scenario.Destination));
        Assert.True((File.GetAttributes(@"\\?\" + scenario.Dest(FakeSoftware.HiddenFile)) & FileAttributes.Hidden) != 0);
        Assert.True((File.GetAttributes(@"\\?\" + scenario.Dest(FakeSoftware.ReadOnlyFile)) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(scenario.Source, @"data\gros.pak")), File.GetLastWriteTimeUtc(scenario.Dest(@"data\gros.pak")));
        Assert.Equal(File.GetCreationTimeUtc(Path.Combine(scenario.Source, "bin")), Directory.GetCreationTimeUtc(scenario.Dest("bin")));
        Assert.Equal(File.ReadAllText(Path.Combine(scenario.Source, FakeSoftware.AdsFile) + ":" + FakeSoftware.AdsName),
            File.ReadAllText(scenario.Dest(FakeSoftware.AdsFile) + ":" + FakeSoftware.AdsName));

        // Liens physiques recréés (même fichier), jonctions recréées à l'identique sans être suivies.
        var a = scenario.Fs.GetEntry(scenario.Dest(FakeSoftware.HardLinkA))!;
        var b = scenario.Fs.GetEntry(scenario.Dest(FakeSoftware.HardLinkB))!;
        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(2u, a.LinkCount);
        var external = scenario.Fs.GetEntry(scenario.Dest(FakeSoftware.ExternalJunction))!;
        Assert.Equal(FsEntryKind.Link, external.Kind);
        Assert.Equal(scenario.Sandbox.Canary, scenario.Fs.ReadReparsePoint(scenario.Dest(FakeSoftware.ExternalJunction), external.Identity).Target);
        Assert.True(scenario.Sandbox.CanaryIntact());
    }

    [Fact]
    public void Destination_stays_private_until_security_is_applied_then_matches_the_source()
    {
        using var scenario = new CopyScenario(fullSoftware: false);
        // Une ACE explicite sur un fichier source : elle doit être recopiée telle quelle.
        var target = Path.Combine(scenario.Source, @"config\settings.json");
        var security = new FileInfo(target).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), FileSystemRights.Read, AccessControlType.Deny));
        new FileInfo(target).SetAccessControl(security);

        var copier = scenario.CopyAll();
        var rootSd = scenario.Fs.GetSecurityDescriptor(scenario.Destination, scenario.DestinationIdentity);
        // Accès privé pendant la copie : exactement SYSTEM, Administrateurs et l'utilisateur courant, héritage coupé.
        Assert.True(SecurityDescriptorTools.IsDaclProtected(rootSd));
        Assert.True(SecurityDescriptorTools.EffectiveDaclEquals(SecurityDescriptorTools.CreatePrivateDirectoryDescriptor(), rootSd));

        Assert.True(scenario.Verify().Success);
        copier.ApplySecurity(scenario.Manifest!, scenario.Destination);

        var result = scenario.Verify(new VerificationOptions { CompareContent = true, CompareSecurity = true });
        Assert.True(result.Success, result.Summary);
        var copied = new FileInfo(scenario.Dest(@"config\settings.json")).GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.Contains(copied, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Value == "S-1-5-32-546");
    }

    public static TheoryData<string, Action<CopyScenario>> Tamperings => new()
    {
        { "octet modifié", s => FlipByte(s.Dest(@"bin\core.dll"), 500_000) },
        { "fichier tronqué", s => Truncate(s.Dest(@"data\gros.pak")) },
        { "fichier supprimé", s => File.Delete(s.Dest(@"bin\plugins\a.dll")) },
        { "fichier caché supprimé", s => DeleteHidden(s.Dest(FakeSoftware.HiddenFile)) },
        { "fichier en trop", s => File.WriteAllText(s.Dest(@"bin\intrus.dll"), "x") },
        { "flux ADS supprimé", s => File.Delete(s.Dest(FakeSoftware.AdsFile) + ":" + FakeSoftware.AdsName) },
        { "flux ADS modifié", s => File.WriteAllText(s.Dest(FakeSoftware.AdsFile) + ":" + FakeSoftware.AdsName, "[ZoneTransfer]\r\nZoneId=4\r\n") },
        { "attribut changé", s => File.SetAttributes(s.Dest(@"bin\app.exe"), FileAttributes.Hidden) },
        { "date changée", s => File.SetLastWriteTimeUtc(s.Dest(@"bin\app.exe"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
        { "fichier substitué", s => Substitute(s.Dest(@"bin\app.exe")) },
        { "lien physique rompu", s => BreakHardLink(s.Dest(FakeSoftware.HardLinkB)) },
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public void Any_tampering_is_detected(string description, Action<CopyScenario> tamper)
    {
        using var scenario = new CopyScenario(fullSoftware: false);
        scenario.CopyAll();
        Assert.True(scenario.Verify().Success);

        tamper(scenario);

        var result = scenario.Verify();
        Assert.False(result.Success, $"Altération non détectée : {description}");
    }

    [Fact]
    public void Quick_verification_without_content_does_not_see_a_flipped_byte()
    {
        // Comportement documenté : seul le mode complet (par défaut) relit le contenu.
        using var scenario = new CopyScenario(fullSoftware: false);
        scenario.CopyAll();
        var lastWrite = File.GetLastWriteTimeUtc(scenario.Dest(@"bin\core.dll"));
        FlipByte(scenario.Dest(@"bin\core.dll"), 10);
        File.SetLastWriteTimeUtc(scenario.Dest(@"bin\core.dll"), lastWrite);

        Assert.True(scenario.Verify(new VerificationOptions { CompareContent = false }).Success);
        Assert.False(scenario.Verify().Success);
    }

    [Fact]
    public void Buffered_and_unbuffered_verification_agree()
    {
        using var scenario = new CopyScenario(fullSoftware: false);
        scenario.CopyAll();
        Assert.True(scenario.Verify(new VerificationOptions { Unbuffered = false }).Success);
        Assert.True(scenario.Verify(new VerificationOptions { Unbuffered = true }).Success);
    }

    [Fact]
    public void Verification_through_a_junction_sees_the_copy()
    {
        using var scenario = new CopyScenario(fullSoftware: false);
        scenario.CopyAll();
        var junction = scenario.Sandbox.PathOf("destination", "acces-par-jonction");
        scenario.Fs.CreateReparsePoint(junction, isDirectory: true, ReparsePointData.CreateJunction(scenario.Destination));

        var result = new TreeVerifier(scenario.Fs).Verify(scenario.Manifest!, junction, new VerificationOptions { FollowRootLink = true, CompareContent = false });
        Assert.True(result.Success, result.Summary);
    }

    [Fact]
    public void Cancellation_stops_the_copy_and_leaves_the_source_intact()
    {
        using var scenario = new CopyScenario(fullSoftware: false);
        var before = TestSandbox.HashTree(scenario.Source);
        using var cts = new CancellationTokenSource();
        var manifest = scenario.Scan();
        var destinationId = scenario.Fs.CreateDirectory(scenario.Destination, SecurityDescriptorTools.CreatePrivateDirectoryDescriptor());
        var copier = new TreeCopier(scenario.Fs, RetryPolicy.None);

        Assert.ThrowsAny<OperationCanceledException>(() => copier.Copy(manifest, scenario.Destination, destinationId, p =>
        {
            if (p.ItemsDone > 5)
            {
                cts.Cancel();
            }
        }, cts.Token));

        Assert.Equal(before, TestSandbox.HashTree(scenario.Source));
        var deletion = new SafeDeleter(scenario.Fs, RetryPolicy.None).DeleteTree(scenario.Destination, destinationId);
        Assert.True(deletion.Complete);
        Assert.False(Directory.Exists(scenario.Destination));
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        var b = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(b ^ 0xFF));
    }

    private static void Truncate(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.SetLength(stream.Length - 1);
    }

    private static void DeleteHidden(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void Substitute(string path)
    {
        var content = File.ReadAllBytes(path);
        var times = (File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        File.WriteAllBytes(path, content);
        File.SetCreationTimeUtc(path, times.Item1);
        File.SetLastWriteTimeUtc(path, times.Item2);
        File.SetAttributes(path, FileAttributes.Normal);
    }

    private static void BreakHardLink(string path)
    {
        var content = File.ReadAllBytes(path);
        var times = (File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        File.WriteAllBytes(path, content);
        File.SetCreationTimeUtc(path, times.Item1);
        File.SetLastWriteTimeUtc(path, times.Item2);
    }
}
