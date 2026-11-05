using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Safety;

namespace Demenageur.Windows.Safety;

/// <summary>
/// Détection des processus qui utilisent un logiciel :
/// <list type="number">
/// <item>processus dont l'exécutable se trouve dans le dossier (chemin réel : un programme lancé via une jonction
/// apparaît avec le chemin de la cible) ;</item>
/// <item>Restart Manager : processus, services et Explorateur (extensions shell) qui retiennent les fichiers donnés.</item>
/// </list>
/// </summary>
public sealed class RestartManagerProcessInspector : IProcessInspector
{
    private const int RmMaxFilesPerCall = 1000;

    public IReadOnlyList<ProcessUsage> FindUsers(IReadOnlyList<string> folders, IReadOnlyList<string> files)
    {
        var result = new Dictionary<int, ProcessUsage>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var image = ImagePathOf(process.Id);
                if (image is not null && folders.Any(f => PathUtil.IsWithinIgnoreCase(image, f)))
                {
                    result[process.Id] = new ProcessUsage(process.Id, process.ProcessName + ".exe", image, null,
                        string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase),
                        "s'exécute depuis ce dossier");
                }
            }
        }

        foreach (var usage in RestartManagerUsers(files))
        {
            result.TryAdd(usage.ProcessId, usage);
        }

        return result.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ProcessUsage> RestartManagerUsers(IReadOnlyList<string> files)
    {
        var result = new List<ProcessUsage>();
        for (var start = 0; start < files.Count; start += RmMaxFilesPerCall)
        {
            var batch = files.Skip(start).Take(RmMaxFilesPerCall).Select(f => PathUtil.ExtendedPrefix + f).ToArray();
            var key = new StringBuilder(64);
            if (RmStartSession(out var session, 0, key) != 0)
            {
                continue;
            }

            try
            {
                if (RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null) != 0)
                {
                    continue;
                }

                uint needed = 0;
                uint count = 0;
                var error = RmGetList(session, out needed, ref count, null, out _);
                if (error is not (0 or ErrorMoreData) || needed == 0)
                {
                    continue;
                }

                var infos = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, out _) != 0)
                {
                    continue;
                }

                foreach (var info in infos.Take((int)count))
                {
                    var isExplorer = info.ApplicationType == RmExplorer || info.AppName.Equals("Explorateur Windows", StringComparison.OrdinalIgnoreCase);
                    result.Add(new ProcessUsage(
                        info.Process.ProcessId,
                        string.IsNullOrWhiteSpace(info.AppName) ? $"Processus {info.Process.ProcessId}" : info.AppName,
                        ImagePathOf(info.Process.ProcessId),
                        string.IsNullOrWhiteSpace(info.ServiceShortName) ? null : info.ServiceShortName,
                        isExplorer || string.Equals(Path.GetFileName(ImagePathOf(info.Process.ProcessId)), "explorer.exe", StringComparison.OrdinalIgnoreCase),
                        info.ApplicationType == RmService ? "(service) utilise des fichiers du logiciel" : "utilise des fichiers du logiciel"));
                }
            }
            finally
            {
                RmEndSession(session);
            }
        }

        return result;
    }

    /// <summary>Chemin complet de l'exécutable d'un processus (accès limité suffisant, y compris pour les processus protégés).</summary>
    public static string? ImagePathOf(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Arrête puis relance les applications (dont l'Explorateur) qui retiennent des fichiers, via Restart Manager.</summary>
    public static IDisposable ShutdownApplicationsUsing(IReadOnlyList<string> files)
    {
        var key = new StringBuilder(64);
        if (RmStartSession(out var session, 0, key) != 0)
        {
            throw new Win32Exception("Restart Manager indisponible.");
        }

        var batch = files.Take(RmMaxFilesPerCall).Select(f => PathUtil.ExtendedPrefix + f).ToArray();
        if (RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null) != 0 || RmShutdown(session, RmForceShutdown, 0) != 0)
        {
            RmEndSession(session);
            throw new Win32Exception("Impossible d'arrêter les applications qui utilisent le logiciel.");
        }

        return new RestartToken(session);
    }

    private sealed class RestartToken(uint session) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            RmRestart(session, 0, 0);
            RmEndSession(session);
        }
    }

    // ─── Interop ───
    private const int ErrorMoreData = 234;
    private const int RmService = 3;
    private const int RmExplorer = 4;
    private const uint RmForceShutdown = 0x1;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo, [In, Out] RmProcessInfo[]? affectedApps, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint sessionHandle, uint actionFlags, nint fnStatus);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmRestart(uint sessionHandle, int restartFlags, nint fnStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
