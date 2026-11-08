using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Demenageur.Windows.SystemServices;

/// <summary>
/// Lance un programme SANS les droits administrateur depuis Déménageur, qui tourne élevé.
/// <para>
/// Indispensable pour l'Explorateur : démarré depuis un processus élevé alors qu'aucun Explorateur ne tourne, il
/// deviendrait le shell de la session avec le jeton administrateur, et tout programme lancé depuis la barre des tâches
/// ou le menu Démarrer hériterait de ces droits, sans invite UAC.
/// </para>
/// <para>
/// Méthode : on reprend le jeton d'un processus de la même session qui tourne déjà sans élévation, au niveau
/// d'intégrité « moyen » (de préférence sihost.exe, l'hôte d'infrastructure du shell, présent même quand l'Explorateur
/// est arrêté), on le duplique en jeton primaire et on lance le programme avec CreateProcessWithTokenW.
/// Le jeton « lié » (TokenLinkedToken) ne convient pas : sans le privilège SeTcb, Windows ne le fournit qu'au niveau
/// « identification », inutilisable pour créer un processus.
/// </para>
/// </summary>
public static class UnelevatedLauncher
{
    /// <summary>Processus préférés comme source du jeton : ils tournent sous l'utilisateur de la session, sans élévation.</summary>
    private static readonly string[] PreferredSources = ["sihost", "ctfmon", "RuntimeBroker", "taskhostw"];

    /// <summary>
    /// Lance <paramref name="exePath"/> avec un jeton non élevé de l'utilisateur de la session.
    /// Renvoie false (sans rien lancer) si aucun jeton convenable n'est trouvé : jamais de repli élevé.
    /// </summary>
    public static bool TryStart(string exePath, out string detail)
    {
        string? launchError = null;
        var found = WithUnelevatedToken((token, name, pid) =>
        {
            try
            {
                Launch(token, exePath);
            }
            catch (Win32Exception ex)
            {
                launchError = $"échec du lancement avec le jeton de {name} (PID {pid}) : {ex.Message}";
            }
        }, out var source);
        detail = !found ? source : launchError ?? $"lancé avec le jeton non élevé de {source}";
        return found && launchError is null;
    }

    /// <summary>
    /// Diagnostic sans rien lancer : décrit le processus dont le jeton serait emprunté, ou pourquoi aucun ne convient.
    /// </summary>
    public static bool TryFindTokenSource(out string description) => WithUnelevatedToken((_, _, _) => { }, out description);

    /// <summary>
    /// Cherche un jeton non élevé de l'utilisateur de la session et appelle <paramref name="use"/> avec lui (puis le ferme).
    /// </summary>
    private static bool WithUnelevatedToken(Action<nint, string, int> use, out string source)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var candidates = new List<(int Pid, string Name, int Rank)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session)
                    {
                        continue;
                    }

                    var rank = Array.FindIndex(PreferredSources, n => n.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase));
                    candidates.Add((process.Id, process.ProcessName, rank < 0 ? int.MaxValue : rank));
                }
                catch (InvalidOperationException)
                {
                    // Processus terminé entre-temps.
                }
            }
        }

        SecurityIdentifier? sessionUser = null;
        foreach (var (pid, name, _) in candidates.OrderBy(c => c.Rank))
        {
            var token = OpenUnelevatedToken(pid, out var user);
            if (token == 0 || user is null)
            {
                continue;
            }

            try
            {
                // L'utilisateur de la session est celui du premier processus convenable (sihost en priorité) :
                // on n'emprunte jamais le jeton d'un autre compte.
                sessionUser ??= user;
                if (!user.Equals(sessionUser))
                {
                    continue;
                }

                use(token, name, pid);
                source = $"{name} (PID {pid}, {user.Value})";
                return true;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        source = "aucun processus non élevé de l'utilisateur trouvé dans la session";
        return false;
    }

    /// <summary>Jeton primaire dupliqué si le processus tourne sans élévation, au niveau moyen, hors AppContainer ; sinon 0.</summary>
    private static nint OpenUnelevatedToken(int pid, out SecurityIdentifier? user)
    {
        user = null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return 0;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate | TokenAssignPrimary, out var token))
            {
                return 0;
            }

            try
            {
                if (ReadInt(token, TokenElevation) != 0
                    || ReadInt(token, TokenIsAppContainer) != 0
                    || IsTokenRestricted(token)
                    || IntegrityRid(token) != MediumIntegrityRid
                    || ReadSid(token, TokenUser) is not { } sid
                    || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
                    || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid))
                {
                    return 0;
                }

                if (!DuplicateTokenEx(token, TokenQuery | TokenDuplicate | TokenAssignPrimary, 0, SecurityImpersonation, TokenPrimary, out var primary))
                {
                    return 0;
                }

                user = sid;
                return primary;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static void Launch(nint token, string exePath)
    {
        if (!CreateEnvironmentBlock(out var environment, token, false))
        {
            throw new Win32Exception();
        }

        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessWithTokenW(token, 0, exePath, null, CreateUnicodeEnvironment, environment, Path.GetDirectoryName(exePath), ref startup, out var info))
            {
                throw new Win32Exception();
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
        finally
        {
            DestroyEnvironmentBlock(environment);
        }
    }

    private static int ReadInt(nint token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            // En cas d'échec, on renvoie -1 : le jeton est alors écarté (prudence).
            return GetTokenInformation(token, informationClass, buffer, sizeof(int), out _) ? Marshal.ReadInt32(buffer) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>SID contenu dans une structure commençant par SID_AND_ATTRIBUTES (TOKEN_USER, TOKEN_MANDATORY_LABEL).</summary>
    private static SecurityIdentifier? ReadSid(nint token, int informationClass)
    {
        GetTokenInformation(token, informationClass, 0, 0, out var length);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            return GetTokenInformation(token, informationClass, buffer, length, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long IntegrityRid(nint token)
    {
        var label = ReadSid(token, TokenIntegrityLevel);
        return label is not null && long.TryParse(label.Value[(label.Value.LastIndexOf('-') + 1)..], out var rid) ? rid : -1;
    }

    // ─── Interop ───
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const long MediumIntegrityRid = 0x2000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool IsTokenRestricted(nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(nint token, int logonFlags, string? applicationName, string? commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint environment);
}
