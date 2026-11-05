namespace Demenageur.Core.Abstractions;

/// <summary>Disponibilité des points de restauration.</summary>
public enum RestorePointAvailability
{
    /// <summary>La protection système est active sur le disque de Windows.</summary>
    Available,

    /// <summary>La protection système est désactivée : Windows ne peut pas créer de point de restauration.</summary>
    ProtectionDisabled,

    /// <summary>Impossible de le savoir.</summary>
    Unknown,
}

/// <summary>Résultat d'une demande de point de restauration.</summary>
/// <param name="Created">Un NOUVEAU point a bien été créé.</param>
/// <param name="Message">Explication pour l'utilisateur (y compris la limite d'un point par 24 h).</param>
public sealed record RestorePointResult(bool Created, string Message);

/// <summary>Points de restauration Windows. L'app ne modifie JAMAIS la configuration de la protection système.</summary>
public interface IRestorePointService
{
    RestorePointAvailability GetAvailability();

    RestorePointResult Create(string description);

    /// <summary>Ouvre le panneau Windows « Protection du système », pour que l'utilisateur l'active lui-même.</summary>
    void OpenSystemProtectionSettings();
}

/// <summary>Alimentation.</summary>
public interface IPowerStatus
{
    /// <summary>Vrai si l'ordinateur fonctionne sur batterie ; pourcentage restant (ou -1).</summary>
    bool IsOnBattery(out int percent);
}

/// <summary>Options de la section critique d'une opération.</summary>
public sealed record CriticalSectionOptions
{
    /// <summary>Arrêter l'Explorateur pendant l'opération (extension shell qui retient des fichiers).</summary>
    public bool StopExplorer { get; init; }

    /// <summary>Services à arrêter pendant l'opération, puis à redémarrer.</summary>
    public IReadOnlyList<string> ServicesToStop { get; init; } = [];

    /// <summary>Fichiers exécutables du logiciel (pour l'arrêt ciblé de l'Explorateur).</summary>
    public IReadOnlyList<string> ExecutableFiles { get; init; } = [];
}
