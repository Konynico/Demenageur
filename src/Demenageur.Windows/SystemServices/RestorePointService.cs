using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Demenageur.Core.Abstractions;
using Microsoft.Win32;

namespace Demenageur.Windows.SystemServices;

/// <summary>
/// Points de restauration : création via <c>SRSetRestorePointW</c>, puis vérification qu'un NOUVEAU point existe
/// (Windows renvoie « succès » sans rien créer si un point date de moins de 24 h). La protection système n'est jamais
/// activée par l'app : si elle est désactivée, on ouvre le panneau Windows pour que l'utilisateur décide.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ModifySettings = 12;

    public RestorePointAvailability GetAvailability()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // RPSessionInterval vaut 0 quand la protection système est désactivée sur tous les disques (lisible sans droits admin).
            using (var restore = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
            {
                if (restore?.GetValue("RPSessionInterval") is int interval && interval == 0)
                {
                    return RestorePointAvailability.ProtectionDisabled;
                }
            }

            // Détail par volume (lisible seulement en administrateur).
            using var clients = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");
            if (clients is null)
            {
                return RestorePointAvailability.ProtectionDisabled;
            }

            // La valeur liste les volumes protégés ; le disque de Windows doit y figurer.
            var systemVolume = SystemVolumeGuid();
            foreach (var name in clients.GetValueNames())
            {
                if (clients.GetValue(name) is string[] volumes
                    && volumes.Any(v => systemVolume is null || v.Contains(systemVolume.Trim('\\').Replace(@"\\?\", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)))
                {
                    return RestorePointAvailability.Available;
                }
            }

            return RestorePointAvailability.ProtectionDisabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RestorePointAvailability.Unknown;
        }
    }

    public RestorePointResult Create(string description)
    {
        if (GetAvailability() == RestorePointAvailability.ProtectionDisabled)
        {
            return new RestorePointResult(false, "La protection système est désactivée sur le disque de Windows : aucun point de restauration ne peut être créé.");
        }

        var before = LatestSequence();
        var info = new RestorePointInfo { EventType = BeginSystemChange, RestorePointType = ModifySettings, Description = Truncate(description) };
        if (!SRSetRestorePointW(ref info, out var status))
        {
            return new RestorePointResult(false, $"Windows a refusé de créer le point de restauration (code {status.Status}).");
        }

        var end = new RestorePointInfo { EventType = EndSystemChange, RestorePointType = ModifySettings, SequenceNumber = status.SequenceNumber };
        SRSetRestorePointW(ref end, out _);

        var after = LatestSequence();
        if (after is not null && before is not null && after <= before)
        {
            return new RestorePointResult(false,
                "Windows n'a pas créé de nouveau point : un point de restauration a déjà été créé il y a moins de 24 heures (limite de Windows). Le plus récent reste disponible.");
        }

        return new RestorePointResult(true, $"Point de restauration « {description} » créé.");
    }

    public void OpenSystemProtectionSettings()
    {
        Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true })?.Dispose();
    }

    private static long? LatestSequence()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
            long? max = null;
            foreach (ManagementObject point in searcher.Get())
            {
                using (point)
                {
                    if (point["SequenceNumber"] is uint sequence)
                    {
                        max = Math.Max(max ?? 0, sequence);
                    }
                }
            }

            return max ?? 0;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    private static string? SystemVolumeGuid()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var buffer = new char[128];
        return GetVolumeNameForVolumeMountPoint(root, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
    }

    private static string Truncate(string text) => text.Length <= 250 ? text : text[..250];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateManagerStatus
    {
        public int Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo restorePoint, out StateManagerStatus status);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, char[] volumeName, int length);
}
