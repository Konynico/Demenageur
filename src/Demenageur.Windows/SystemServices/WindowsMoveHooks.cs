using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Demenageur.Core.Abstractions;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Windows.Safety;

namespace Demenageur.Windows.SystemServices;

/// <summary>
/// Section critique d'une opération, côté Windows :
/// <list type="bullet">
/// <item>anti-veille pendant toute l'opération ;</item>
/// <item>arrêt des services choisis par l'utilisateur, puis redémarrage ;</item>
/// <item>arrêt de l'Explorateur (Restart Manager) si l'utilisateur l'a accepté, puis relance — un fichier témoin
/// permet de le relancer au démarrage suivant si l'app plantait entre les deux ;</item>
/// <item>dernière vérification des processus juste avant le verrou.</item>
/// </list>
/// </summary>
public sealed class WindowsMoveHooks : IMoveHooks
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly CriticalSectionOptions _options;
    private readonly IProcessInspector _processes;
    private readonly string _explorerFlagFile;
    private readonly List<string> _stoppedServices = [];
    private IDisposable? _explorer;

    public WindowsMoveHooks(CriticalSectionOptions options, IProcessInspector processes, string dataDirectory)
    {
        _options = options;
        _processes = processes;
        _explorerFlagFile = Path.Combine(dataDirectory, ExplorerFlagFileName);
    }

    public const string ExplorerFlagFileName = "explorateur-arrete.temoin";

    public string? CheckBeforeLock(string folderToLock)
    {
        var users = _processes.FindUsers([folderToLock], _options.ExecutableFiles);
        var blocking = users.Where(u => !(u.IsExplorer && _options.StopExplorer)).ToList();
        return blocking.Count == 0
            ? null
            : "Le logiciel est encore utilisé : " + string.Join(", ", blocking.Select(b => $"{b.Name} (PID {b.ProcessId})")) + ". Ferme-le puis recommence. Rien n'a été modifié.";
    }

    public void EnterCriticalSection()
    {
        SetThreadExecutionState(EsContinuous | EsSystemRequired);

        foreach (var name in _options.ServicesToStop)
        {
            using var service = new ServiceController(name);
            if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                _stoppedServices.Add(name);
            }
        }

        if (_options.StopExplorer && _options.ExecutableFiles.Count > 0)
        {
            File.WriteAllText(_explorerFlagFile, DateTimeOffset.Now.ToString("O"));
            _explorer = RestartManagerProcessInspector.ShutdownApplicationsUsing(_options.ExecutableFiles);
        }
    }

    public void LeaveCriticalSection()
    {
        try
        {
            if (_explorer is not null)
            {
                _explorer.Dispose();
                _explorer = null;
                EnsureExplorerRunning();
                File.Delete(_explorerFlagFile);
            }

            foreach (var name in _stoppedServices)
            {
                try
                {
                    using var service = new ServiceController(name);
                    service.Start();
                }
                catch (InvalidOperationException)
                {
                    // Le service ne redémarre pas : signalé par le bilan de santé ; on continue.
                }
            }

            _stoppedServices.Clear();
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
        }
    }

    /// <summary>Au démarrage de l'app : si l'Explorateur a été arrêté par une opération interrompue, le relancer.</summary>
    public static void RecoverExplorer(string dataDirectory)
    {
        var flag = Path.Combine(dataDirectory, ExplorerFlagFileName);
        if (!File.Exists(flag))
        {
            return;
        }

        EnsureExplorerRunning();
        File.Delete(flag);
    }

    private static void EnsureExplorerRunning()
    {
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true })?.Dispose();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>État de l'alimentation (batterie).</summary>
public sealed class WindowsPowerStatus : IPowerStatus
{
    public bool IsOnBattery(out int percent)
    {
        percent = -1;
        if (!GetSystemPowerStatus(out var status))
        {
            return false;
        }

        percent = status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent;
        return status.AcLineStatus == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
