using System.Runtime.InteropServices;

namespace Demenageur.Windows.SystemServices;

/// <summary>Une seule instance de Déménageur à la fois, sur toute la machine (mutex global).</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\Demenageur-7F3A2C1E-instance-unique";
    private readonly Mutex _mutex;
    private readonly bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public bool IsFirstInstance => _owned;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // une instance précédente s'est arrêtée brutalement
        }

        return new SingleInstance(mutex, owned);
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
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
