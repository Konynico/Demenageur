using System.ComponentModel;
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

    /// <summary>
    /// Texte à montrer à l'utilisateur si l'Explorateur n'a pas pu être relancé sans élévation (null sinon).
    /// </summary>
    public string? ExplorerWarning { get; private set; }

    /// <summary>Détail technique de la relance de l'Explorateur, pour les logs (null si elle n'a pas eu lieu).</summary>
    public string? ExplorerRestartDetail { get; private set; }

    /// <summary>Consigne affichée quand l'Explorateur n'a pas pu être relancé sans élévation.</summary>
    public const string ExplorerRestartAdvice =
        "L'Explorateur (barre des tâches, bureau) n'a pas pu être relancé sans les droits administrateur, et Déménageur "
        + "refuse de le relancer en administrateur. Relance-le toi-même : Ctrl+Maj+Échap → « Exécuter une nouvelle tâche » → "
        + "explorer (sans cocher « Créer cette tâche avec des privilèges d'administration »), ou ferme puis rouvre ta session.";

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
        try
        {
            foreach (var name in _options.ServicesToStop)
            {
                using var service = new ServiceController(name);
                if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    service.Stop();
                    _stoppedServices.Add(name); // avant l'attente : redémarré même s'il ne s'arrête pas dans les temps
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
            }

            if (_options.StopExplorer && _options.ExecutableFiles.Count > 0)
            {
                // Restart Manager ferme DE FORCE tout ce qui utilise ces fichiers : on ne l'autorise que si l'Explorateur
                // est le seul concerné. Un logiciel relancé depuis la simulation n'est jamais tué sans sauvegarde.
                var others = _processes.FindUsers([], _options.ExecutableFiles).Where(u => !u.IsExplorer).ToList();
                if (others.Count > 0)
                {
                    throw new OperationAbortedException("Le logiciel est encore utilisé : "
                        + string.Join(", ", others.Select(o => $"{o.Name} (PID {o.ProcessId})")) + ". Ferme-le puis recommence. Rien n'a été modifié.");
                }

                File.WriteAllText(_explorerFlagFile, DateTimeOffset.Now.ToString("O"));
                _explorer = RestartManagerProcessInspector.ShutdownApplicationsUsing(_options.ExecutableFiles);
            }
        }
        catch
        {
            LeaveCriticalSection(); // remet en marche ce qui a déjà été arrêté
            throw;
        }
    }

    /// <summary>
    /// Sortie de section critique : peut être appelée plusieurs fois, et ne lève jamais d'exception — chaque étape
    /// (Explorateur, services, anti-veille) est tentée même si la précédente échoue.
    /// </summary>
    public void LeaveCriticalSection()
    {
        try
        {
            RestoreExplorer();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ExplorerRestartDetail = "relance impossible : " + ex.Message;
            ExplorerWarning = ExplorerRestartAdvice;
        }
        finally
        {
            foreach (var name in _stoppedServices)
            {
                try
                {
                    using var service = new ServiceController(name);
                    if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                    {
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        service.Start();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
                {
                    // Le service ne redémarre pas : signalé par le bilan de santé ; on continue.
                }
            }

            _stoppedServices.Clear();
            SetThreadExecutionState(EsContinuous);
        }
    }

    private void RestoreExplorer()
    {
        if (_explorer is null)
        {
            return;
        }

        var token = _explorer;
        _explorer = null;
        try
        {
            token.Dispose();
        }
        finally
        {
            var restarted = EnsureExplorerRunning(out var detail);
            ExplorerRestartDetail = detail;
            if (restarted)
            {
                File.Delete(_explorerFlagFile);
            }
            else
            {
                // Fichier témoin conservé : nouvel essai au prochain démarrage de Déménageur.
                ExplorerWarning = ExplorerRestartAdvice;
            }
        }
    }

    /// <summary>
    /// Au démarrage de l'app : si l'Explorateur a été arrêté par une opération interrompue, le relancer (sans élévation).
    /// Renvoie la consigne à afficher s'il n'a pas pu l'être, sinon null.
    /// </summary>
    public static string? RecoverExplorer(string dataDirectory, out string? detail)
    {
        detail = null;
        var flag = Path.Combine(dataDirectory, ExplorerFlagFileName);
        if (!File.Exists(flag))
        {
            return null;
        }

        if (!EnsureExplorerRunning(out detail))
        {
            return ExplorerRestartAdvice;
        }

        File.Delete(flag);
        return null;
    }

    /// <summary>
    /// Relance l'Explorateur s'il ne tourne pas, TOUJOURS sans élévation (jamais Process.Start depuis notre processus
    /// administrateur). Renvoie false s'il n'est pas lancé : aucun jeton non élevé trouvé, ou échec du lancement.
    /// </summary>
    private static bool EnsureExplorerRunning(out string detail)
    {
        if (Process.GetProcessesByName("explorer").Length > 0)
        {
            detail = "l'Explorateur tournait déjà";
            return true;
        }

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return UnelevatedLauncher.TryStart(explorer, out detail);
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
