using Demenageur.Core.Safety;
using Demenageur.Windows.FileSystem;
using Demenageur.Windows.Safety;

namespace Demenageur.Tests.Safety;

/// <summary>Inspecteurs réels, en lecture seule, sur cette machine.</summary>
public sealed class WindowsInspectorTests
{
    [Fact]
    public void System_volume_is_internal_ntfs()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var details = new WindowsVolumeInspector(new Win32FileSystem()).Describe(windows);
        Assert.True(details.Volume.IsNtfs);
        Assert.True(details.IsSystemVolume);
        Assert.Equal(DiskBusType.Internal, details.BusType);
        Assert.False(details.IsSubst);
    }

    [Theory]
    [InlineData("D:")]
    [InlineData("E:")]
    public void Data_volumes_of_this_machine_are_seen_as_internal(string letter)
    {
        Assert.SkipUnless(Directory.Exists(letter + "\\"), $"Pas de lecteur {letter} sur cette machine.");
        var details = new WindowsVolumeInspector(new Win32FileSystem()).Describe(letter + "\\");
        Assert.True(details.BusType == DiskBusType.Internal, $"{letter} vu comme {details.BusType} ({details.BusDescription}).");
    }

    [Fact]
    public void System_state_can_be_read()
    {
        var snapshot = new WindowsSystemInspector().Capture();
        Assert.NotEmpty(snapshot.Services);
        Assert.Contains(snapshot.Services, s => s.IsDriver && s.ImagePath is not null && s.ImagePath.EndsWith(".sys", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(snapshot.ComServers);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\acpi.sys", @"System32\drivers\acpi.sys")]
    [InlineData(@"system32\drivers\x.sys", @"system32\drivers\x.sys")]
    [InlineData("\"C:\\Program Files\\App\\svc.exe\" -service", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Program Files\App\svc.exe -k", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"\??\C:\Pilotes\filtre.sys", @"C:\Pilotes\filtre.sys")]
    public void Service_image_paths_are_normalized(string raw, string expectedEnd)
    {
        var normalized = new WindowsSystemInspector().NormalizeImagePath(raw);
        Assert.NotNull(normalized);
        Assert.EndsWith(expectedEnd, normalized, StringComparison.OrdinalIgnoreCase);
    }
}
