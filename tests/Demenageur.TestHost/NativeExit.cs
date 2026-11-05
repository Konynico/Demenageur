using System.Runtime.InteropServices;

namespace Demenageur.TestHost;

/// <summary>Terminaison immédiate du processus (équivalent logiciel d'une coupure de courant pour le code en cours).</summary>
internal static partial class NativeExit
{
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    public static void TerminateNow(uint exitCode)
    {
        TerminateProcess(GetCurrentProcess(), exitCode);
        while (true)
        {
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
