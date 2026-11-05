using System.Buffers.Binary;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Demenageur.Windows.Safety;

/// <summary>
/// Détails des volumes : type de bus lu via les extents du volume puis la propriété de chaque disque physique
/// (fonctionne aussi pour un volume dynamique ou Storage Spaces), lecteur « subst », chiffrement BitLocker.
/// </summary>
public sealed unsafe class WindowsVolumeInspector : IVolumeInspector
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private readonly IFileSystem _fs;
    private readonly string _systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

    public WindowsVolumeInspector(IFileSystem fs)
    {
        _fs = fs;
    }

    public VolumeDetails Describe(string path) => Describe(path, includeBitLocker: true);

    /// <summary>BitLocker (requête WMI lente) n'est interrogé que pour un disque cible, pas pour le bandeau des disques.</summary>
    public VolumeDetails Describe(string path, bool includeBitLocker)
    {
        var volume = _fs.GetVolumeInfo(path);
        var letter = volume.RootPath.Length >= 2 && volume.RootPath[1] == ':' ? volume.RootPath[..2] : null;
        var (bus, description) = letter is null ? (DiskBusType.Unknown, "inconnu") : BusTypeOf(letter);
        if (volume.DriveType == VolumeDriveType.Remote)
        {
            (bus, description) = (DiskBusType.Network, "réseau");
        }
        else if (volume.DriveType == VolumeDriveType.Removable && bus != DiskBusType.Usb)
        {
            (bus, description) = (DiskBusType.Removable, "amovible");
        }

        var isSystem = string.Equals(volume.RootPath, _systemRoot, StringComparison.OrdinalIgnoreCase);
        return new VolumeDetails
        {
            Volume = volume,
            BusType = bus,
            BusDescription = description,
            IsSubst = letter is not null && IsSubst(letter),
            IsSystemVolume = isSystem,
            BitLockerRisk = letter is null || isSystem || !includeBitLocker ? null : BitLockerRisk(letter),
        };
    }

    public IReadOnlyList<VolumeDetails> ListVolumes()
    {
        var result = new List<VolumeDetails>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    result.Add(Describe(drive.RootDirectory.FullName, includeBitLocker: false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lecteur inaccessible : ignoré.
            }
        }

        return result;
    }

    private static (DiskBusType, string) BusTypeOf(string letter)
    {
        try
        {
            var disks = DiskNumbersOf(letter);
            if (disks.Count == 0)
            {
                return (DiskBusType.Unknown, "inconnu");
            }

            var types = disks.Select(BusOfDisk).ToList();
            var worst = types.OrderByDescending(t => t.Item1 switch
            {
                DiskBusType.Usb => 5,
                DiskBusType.Removable => 4,
                DiskBusType.Network => 3,
                DiskBusType.Virtual => 2,
                DiskBusType.Unknown => 1,
                _ => 0,
            }).First();
            return worst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (DiskBusType.Unknown, "inconnu");
        }
    }

    private static List<int> DiskNumbersOf(string letter)
    {
        using var volume = OpenDevice(@"\\.\" + letter);
        if (volume.IsInvalid)
        {
            return [];
        }

        var buffer = stackalloc byte[1024];
        if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, null, 0, buffer, 1024, out _, 0))
        {
            return [];
        }

        var span = new ReadOnlySpan<byte>(buffer, 1024);
        var count = BinaryPrimitives.ReadInt32LittleEndian(span);
        var result = new List<int>();
        for (var i = 0; i < count && 8 + (i * 24) + 4 <= span.Length; i++)
        {
            result.Add(BinaryPrimitives.ReadInt32LittleEndian(span[(8 + (i * 24))..]));
        }

        return result.Distinct().ToList();
    }

    private static (DiskBusType, string) BusOfDisk(int disk)
    {
        using var device = OpenDevice($@"\\.\PhysicalDrive{disk}");
        if (device.IsInvalid)
        {
            return (DiskBusType.Unknown, "inconnu");
        }

        var query = stackalloc byte[12]; // STORAGE_PROPERTY_QUERY : StorageDeviceProperty, PropertyStandardQuery
        new Span<byte>(query, 12).Clear();
        var output = stackalloc byte[1024];
        if (!DeviceIoControl(device, IoctlStorageQueryProperty, query, 12, output, 1024, out var returned, 0) || returned < 32)
        {
            return (DiskBusType.Unknown, "inconnu");
        }

        var span = new ReadOnlySpan<byte>(output, (int)returned);
        var removable = span[10] != 0;
        var bus = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
        return bus switch
        {
            7 => (DiskBusType.Usb, "USB"),
            12 or 13 => (DiskBusType.Removable, "carte mémoire"),
            14 or 15 => (DiskBusType.Virtual, "virtuel (VHD)"),
            9 => (DiskBusType.Network, "iSCSI (réseau)"),
            17 => (DiskBusType.Internal, "NVMe interne"),
            11 => (DiskBusType.Internal, "SATA interne"),
            10 => (DiskBusType.Internal, "SAS interne"),
            8 => (DiskBusType.Internal, "RAID interne"),
            16 => (DiskBusType.Internal, "espaces de stockage"),
            1 or 3 or 2 or 18 => (DiskBusType.Internal, "interne"),
            _ when removable => (DiskBusType.Removable, "amovible"),
            _ => (DiskBusType.Unknown, "inconnu"),
        };
    }

    private static bool IsSubst(string letter)
    {
        var buffer = new StringBuilder(1024);
        return QueryDosDevice(letter, buffer, buffer.Capacity) > 0 && buffer.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
    }

    /// <summary>Vrai si le volume est protégé par BitLocker sans déverrouillage automatique (null si inconnu).</summary>
    private static bool? BitLockerRisk(string letter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'");
            foreach (ManagementObject volume in searcher.Get())
            {
                using (volume)
                {
                    if (volume["ProtectionStatus"] is not uint protection || protection != 1)
                    {
                        return false;
                    }

                    var autoUnlock = volume.InvokeMethod("IsAutoUnlockEnabled", null, null);
                    return autoUnlock?["IsAutoUnlockEnabled"] is not true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    private static SafeFileHandle OpenDevice(string path)
        => CreateFile(path, 0, 3, 0, 3, 0, 0);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
