using System.IO.Hashing;
using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.FileSystem;

public sealed class Win32FileSystemTests
{
    [Fact]
    public void CreateDirectory_fails_if_it_already_exists()
    {
        using var sandbox = new TestSandbox();
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        Directory.CreateDirectory(sandbox.PathOf("zone"));
        fs.CreateDirectory(sandbox.PathOf("zone", "a"));
        var ex = Assert.Throws<FileSystemException>(() => fs.CreateDirectory(sandbox.PathOf("zone", "a")));
        Assert.Equal(FsErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public void Rename_keeps_identity_and_never_replaces()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "logiciel", "bin"));
        Directory.CreateDirectory(Path.Combine(zone, "occupe"));
        var fs = sandbox.Guarded(zone);
        var before = fs.GetEntry(Path.Combine(zone, "logiciel"))!;

        var ex = Assert.Throws<FileSystemException>(() => fs.Rename(Path.Combine(zone, "logiciel"), before.Identity, "occupe"));
        Assert.Equal(FsErrorKind.AlreadyExists, ex.Kind);

        fs.Rename(Path.Combine(zone, "logiciel"), before.Identity, "logiciel.dmv-old-1");
        var after = fs.GetEntry(Path.Combine(zone, "logiciel.dmv-old-1"))!;
        Assert.Equal(before.Identity, after.Identity);
        Assert.Null(fs.GetEntry(Path.Combine(zone, "logiciel")));
    }

    [Fact]
    public void Rename_with_wrong_identity_is_refused()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "a"));
        Directory.CreateDirectory(Path.Combine(zone, "b"));
        var fs = sandbox.Guarded(zone);
        var other = fs.GetEntry(Path.Combine(zone, "b"))!;
        var ex = Assert.Throws<FileSystemException>(() => fs.Rename(Path.Combine(zone, "a"), other.Identity, "c"));
        Assert.Equal(FsErrorKind.IdentityMismatch, ex.Kind);
        Assert.True(Directory.Exists(Path.Combine(zone, "a")));
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void Rename_lock_fails_while_a_file_inside_is_open(FileShare share)
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "logiciel", "bin"));
        File.WriteAllText(Path.Combine(zone, "logiciel", "bin", "app.exe"), "x");
        var fs = sandbox.Guarded(zone);
        var info = fs.GetEntry(Path.Combine(zone, "logiciel"))!;

        using (new FileStream(Path.Combine(zone, "logiciel", "bin", "app.exe"), FileMode.Open, FileAccess.Read, share))
        {
            Assert.Throws<FileSystemException>(() => fs.Rename(Path.Combine(zone, "logiciel"), info.Identity, "logiciel.dmv-old"));
        }

        Assert.True(Directory.Exists(Path.Combine(zone, "logiciel")));
        Assert.False(Directory.Exists(Path.Combine(zone, "logiciel.dmv-old")));
    }

    [Fact]
    public void Rename_lock_fails_while_a_process_has_its_current_directory_inside()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "logiciel", "bin"));
        var fs = sandbox.Guarded(zone);
        var info = fs.GetEntry(Path.Combine(zone, "logiciel"))!;

        using var process = TestInterop.StartProcessWithCurrentDirectory(Path.Combine(zone, "logiciel", "bin"));
        try
        {
            Thread.Sleep(300);
            Assert.Throws<FileSystemException>(() => fs.Rename(Path.Combine(zone, "logiciel"), info.Identity, "logiciel.dmv-old"));
            Assert.True(Directory.Exists(Path.Combine(zone, "logiciel")));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    [Fact]
    public void Delete_removes_read_only_files_and_refuses_non_empty_directories()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "d"));
        var file = Path.Combine(zone, "d", "lecture-seule.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        var fs = sandbox.Guarded(zone);

        var dir = fs.GetEntry(Path.Combine(zone, "d"))!;
        var ex = Assert.Throws<FileSystemException>(() => fs.Delete(Path.Combine(zone, "d"), dir.Identity));
        Assert.Equal(FsErrorKind.DirectoryNotEmpty, ex.Kind);

        fs.Delete(file, fs.GetEntry(file)!.Identity);
        Assert.False(File.Exists(file));
        fs.Delete(Path.Combine(zone, "d"), dir.Identity);
        Assert.False(Directory.Exists(Path.Combine(zone, "d")));
    }

    [Fact]
    public void Our_junction_is_byte_identical_to_mklink_J()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        var target = Path.Combine(zone, "cible avec espaces é");
        Directory.CreateDirectory(target);
        TestInterop.MklinkJunction(Path.Combine(zone, "par-mklink"), target);
        var fs = sandbox.Guarded(zone);

        var ours = fs.CreateReparsePoint(Path.Combine(zone, "par-demenageur"), isDirectory: true, ReparsePointData.CreateJunction(target));
        var mklinkInfo = fs.GetEntry(Path.Combine(zone, "par-mklink"))!;

        var mklinkData = fs.ReadReparsePoint(Path.Combine(zone, "par-mklink"), mklinkInfo.Identity);
        var ourData = fs.ReadReparsePoint(Path.Combine(zone, "par-demenageur"), ours);
        Assert.Equal(Convert.ToHexString(mklinkData.Raw), Convert.ToHexString(ourData.Raw));
        Assert.True(ourData.IsJunction);
        Assert.Equal(target, ourData.Target);

        // La jonction se traverse : un fichier créé dans la cible est visible à travers elle.
        File.WriteAllText(Path.Combine(target, "f.txt"), "contenu");
        Assert.Equal("contenu", File.ReadAllText(Path.Combine(zone, "par-demenageur", "f.txt")));
        Assert.Equal(FsEntryKind.Link, fs.GetEntry(Path.Combine(zone, "par-demenageur"))!.Kind);
    }

    [Fact]
    public void Hash_is_identical_buffered_and_unbuffered_and_matches_content()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(zone);
        var data = new byte[(3 << 20) + 4097];
        new Random(1).NextBytes(data);
        var path = Path.Combine(zone, "donnees.bin");
        File.WriteAllBytes(path, data);
        var fs = sandbox.Guarded(zone);
        var id = fs.GetEntry(path)!.Identity;

        var expected = XxHash128.HashToUInt128(data);
        Assert.Equal(expected, fs.HashStream(path, null, id, unbuffered: false, null, default).Value);
        Assert.Equal(expected, fs.HashStream(path, null, id, unbuffered: true, null, default).Value);
    }

    [Fact]
    public void Lists_alternate_streams_and_hashes_them()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(zone);
        var path = Path.Combine(zone, "setup.exe");
        File.WriteAllText(path, "programme");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var fs = sandbox.Guarded(zone);
        var id = fs.GetEntry(path)!.Identity;

        var stream = Assert.Single(fs.ListAlternateStreams(path, id));
        Assert.Equal("Zone.Identifier", stream.Name);
        var expected = XxHash128.HashToUInt128(File.ReadAllBytes(path + ":Zone.Identifier"));
        Assert.Equal(expected, fs.HashStream(path, "Zone.Identifier", id, unbuffered: true, null, default).Value);
    }

    [Fact]
    public void Tricky_names_are_handled_exactly()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        var software = new FakeSoftwareBuilder(Path.Combine(zone, "app")) { IncludeJunctions = false, IncludeHardLinks = false }.Build();
        var fs = sandbox.Guarded(zone);
        foreach (var name in new[] { "a.", "b ", "CON", "é.txt", "é.txt", "iso\uD800le.txt" })
        {
            var path = Path.Combine(software.Root, "noms", name);
            var info = fs.GetEntry(path);
            Assert.NotNull(info);
            fs.Delete(path, info.Identity);
            Assert.Null(fs.GetEntry(path));
        }
    }

    [Fact]
    public void Security_descriptor_round_trip_keeps_protection_flag()
    {
        using var sandbox = new TestSandbox();
        var zone = sandbox.PathOf("zone");
        Directory.CreateDirectory(Path.Combine(zone, "a"));
        Directory.CreateDirectory(Path.Combine(zone, "b"));
        var fs = sandbox.Guarded(zone);

        var me = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        var a = fs.GetEntry(Path.Combine(zone, "a"))!;
        fs.SetSecurityDescriptor(Path.Combine(zone, "a"), a.Identity, security.GetSecurityDescriptorBinaryForm(), SecurityParts.Dacl);

        var sdA = new RawSecurityDescriptor(fs.GetSecurityDescriptor(Path.Combine(zone, "a"), a.Identity), 0);
        Assert.True((sdA.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);

        // Copie verbatim vers b : même DACL, même protection.
        var b = fs.GetEntry(Path.Combine(zone, "b"))!;
        fs.SetSecurityDescriptor(Path.Combine(zone, "b"), b.Identity, fs.GetSecurityDescriptor(Path.Combine(zone, "a"), a.Identity), SecurityParts.Dacl);
        var sdB = new RawSecurityDescriptor(fs.GetSecurityDescriptor(Path.Combine(zone, "b"), b.Identity), 0);
        Assert.Equal(sdA.GetSddlForm(AccessControlSections.Access), sdB.GetSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Volume_info_reports_ntfs_for_the_temp_volume()
    {
        using var sandbox = new TestSandbox();
        var fs = sandbox.Guarded(sandbox.PathOf("zone"));
        var volume = fs.GetVolumeInfo(sandbox.PathOf("zone", "pas-encore-cree"));
        Assert.True(volume.IsNtfs);
        Assert.True(volume.ClusterSize >= 512);
        Assert.NotNull(volume.VolumeGuidPath);
        Assert.Equal(VolumeDriveType.Fixed, volume.DriveType);
    }
}
