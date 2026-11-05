using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Safety;

/// <summary>Gravité d'une règle.</summary>
public enum RuleLevel
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Avertissement : le déplacement reste possible.</summary>
    Warning,

    /// <summary>Confirmation renforcée : il faut taper le nom du logiciel pour continuer.</summary>
    Confirm,

    /// <summary>Bloqué, sans dérogation possible.</summary>
    Block,
}

/// <summary>Résultat d'une règle ou d'une vérification.</summary>
public sealed record RuleVerdict(string RuleId, RuleLevel Level, string Title, string Explanation);

/// <summary>Service ou pilote Windows (lu dans le registre).</summary>
public sealed record ServiceInfo(string Name, string DisplayName, bool IsDriver, int StartType, string? ImagePath, string? ServiceDll);

/// <summary>Logiciel de sécurité déclaré au Centre de sécurité Windows.</summary>
public sealed record SecurityProduct(string Name, IReadOnlyList<string> Paths);

/// <summary>Référence système vers un fichier (fournisseur d'identification, paquet LSA, Winlogon, IFEO…).</summary>
public sealed record SystemHook(string Kind, string Description, string Path);

/// <summary>Processus qui utilise des fichiers d'un dossier.</summary>
public sealed record ProcessUsage(int ProcessId, string Name, string? ImagePath, string? ServiceName, bool IsExplorer, string Reason);

/// <summary>Type de bus d'un disque.</summary>
public enum DiskBusType
{
    Unknown,
    Internal,
    Usb,
    Virtual,
    Removable,
    Network,
}

/// <summary>Volume, vu pour un déplacement.</summary>
public sealed record VolumeDetails
{
    public required VolumeInfo Volume { get; init; }

    public required DiskBusType BusType { get; init; }

    /// <summary>Description lisible du bus (NVMe, SATA, USB…).</summary>
    public required string BusDescription { get; init; }

    public bool IsSubst { get; init; }

    public bool IsSystemVolume { get; init; }

    /// <summary>Chiffré par BitLocker, verrouillé ou sans déverrouillage automatique (null = inconnu).</summary>
    public bool? BitLockerRisk { get; init; }

    public string Letter => Volume.RootPath.Length >= 2 ? Volume.RootPath[..2] : Volume.RootPath;
}

/// <summary>État de la machine utile aux règles (lu une fois, mis en cache par l'appelant).</summary>
public sealed record SystemSnapshot
{
    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];

    public IReadOnlyList<SecurityProduct> SecurityProducts { get; init; } = [];

    public IReadOnlyList<SystemHook> SystemHooks { get; init; } = [];

    /// <summary>Composants COM / extensions du shell (chemin du serveur).</summary>
    public IReadOnlyList<SystemHook> ComServers { get; init; } = [];

    /// <summary>Tâches planifiées (nom, contenu textuel de la définition).</summary>
    public IReadOnlyList<(string Name, string Definition)> ScheduledTasks { get; init; } = [];

    /// <summary>Chemins concernés par des renommages en attente de redémarrage.</summary>
    public IReadOnlyList<string> PendingRenames { get; init; } = [];
}

/// <summary>Lecture de l'état système (implémentée par Demenageur.Windows, simulée dans les tests).</summary>
public interface ISystemInspector
{
    SystemSnapshot Capture();
}

/// <summary>Processus et services qui utilisent des fichiers.</summary>
public interface IProcessInspector
{
    /// <summary>Processus dont l'exécutable est dans l'un des dossiers, ou qui retiennent l'un des fichiers (Restart Manager).</summary>
    IReadOnlyList<ProcessUsage> FindUsers(IReadOnlyList<string> folders, IReadOnlyList<string> files);
}

/// <summary>Détails des volumes (bus, subst, BitLocker…).</summary>
public interface IVolumeInspector
{
    VolumeDetails Describe(string path);

    IReadOnlyList<VolumeDetails> ListVolumes();
}
