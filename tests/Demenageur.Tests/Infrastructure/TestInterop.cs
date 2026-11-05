using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Demenageur.Tests.Infrastructure;

/// <summary>Petites fonctions Windows utiles pour préparer les dossiers factices.</summary>
internal static partial class TestInterop
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkNative(string fileName, string existingFileName, nint securityAttributes);

    public static void CreateHardLink(string newLink, string existing)
    {
        TestSandbox.AssertInsideTestBase(newLink);
        if (!CreateHardLinkNative(@"\\?\" + newLink, @"\\?\" + existing, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Crée une jonction avec <c>mklink /J</c>, exactement comme le ferait l'utilisateur.</summary>
    public static void MklinkJunction(string link, string target)
    {
        TestSandbox.AssertInsideTestBase(link);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink /J a échoué : {error}");
        }
    }

    /// <summary>Lance un processus enfant dont le dossier courant est <paramref name="workingDirectory"/> (il le garde ouvert).</summary>
    public static Process StartProcessWithCurrentDirectory(string workingDirectory)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        return Process.Start(psi)!;
    }
}
