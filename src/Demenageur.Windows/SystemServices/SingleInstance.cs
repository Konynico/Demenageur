using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Demenageur.Windows.SystemServices;

/// <summary>Résultat de la demande d'instance unique.</summary>
public enum InstanceState
{
    /// <summary>Aucune autre instance : on peut démarrer.</summary>
    First,

    /// <summary>Déménageur est déjà ouvert.</summary>
    AlreadyRunning,

    /// <summary>
    /// L'objet d'instance unique existe mais a été créé par un autre compte (non administrateur) : il pourrait servir à
    /// empêcher Déménageur de démarrer, ou à le faire croire ouvert. On refuse de démarrer et on le dit.
    /// </summary>
    Blocked,
}

/// <summary>
/// Une seule instance de Déménageur à la fois, sur toute la machine (mutex global). Le mutex est créé avec des droits
/// réservés aux administrateurs et à SYSTEM ; s'il existe déjà, son propriétaire est vérifié.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\Demenageur-7F3A2C1E-instance-unique";
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    private readonly Mutex? _mutex;
    private readonly bool _owned;

    private SingleInstance(Mutex? mutex, InstanceState state)
    {
        _mutex = mutex;
        State = state;
        _owned = state == InstanceState.First;
    }

    public InstanceState State { get; }

    public bool IsFirstInstance => State == InstanceState.First;

    /// <summary>Message à afficher quand <see cref="State"/> vaut <see cref="InstanceState.Blocked"/>.</summary>
    public const string BlockedMessage =
        "Déménageur ne peut pas démarrer : un autre programme, lancé sans les droits administrateur, occupe l'objet système "
        + "qui garantit qu'un seul Déménageur tourne à la fois. Redémarre l'ordinateur puis relance Déménageur. "
        + "Si le problème revient, un programme indésirable tente peut-être de le bloquer.";

    public static SingleInstance Acquire()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        // Version élevée : seuls les administrateurs et SYSTEM. Aperçu (non élevé) : l'utilisateur courant en plus.
        var trusted = new HashSet<SecurityIdentifier> { AdministratorsSid, SystemSid };
        if (!elevated)
        {
            trusted.Add(identity.User!);
        }

        var security = new MutexSecurity();
        if (elevated)
        {
            security.SetOwner(AdministratorsSid); // propriétaire explicite : vérifiable par l'instance suivante
        }

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in trusted)
        {
            security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
        }

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, MutexName, out createdNew, security);
        }
        catch (UnauthorizedAccessException)
        {
            // Existe déjà et nous en refuse l'accès : soit une version élevée tourne (vu depuis l'aperçu), soit un squatteur.
            return new SingleInstance(null, elevated ? InstanceState.Blocked : InstanceState.AlreadyRunning);
        }

        if (!createdNew && !HasTrustedOwner(mutex, trusted))
        {
            mutex.Dispose();
            return new SingleInstance(null, InstanceState.Blocked);
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // une instance précédente s'est arrêtée brutalement
        }

        if (!owned)
        {
            mutex.Dispose();
            return new SingleInstance(null, InstanceState.AlreadyRunning);
        }

        return new SingleInstance(mutex, InstanceState.First);
    }

    private static bool HasTrustedOwner(Mutex mutex, IReadOnlySet<SecurityIdentifier> trusted)
    {
        try
        {
            return mutex.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && trusted.Contains(owner);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex!.ReleaseMutex();
        }

        _mutex?.Dispose();
    }
}

/// <summary>Empêche l'arrêt ou la déconnexion de Windows pendant une opération (message affiché par Windows).</summary>
public static class ShutdownBlocker
{
    public static void Block(nint windowHandle, string reason) => ShutdownBlockReasonCreate(windowHandle, reason);

    public static void Release(nint windowHandle) => ShutdownBlockReasonDestroy(windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(nint hWnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(nint hWnd);
}
