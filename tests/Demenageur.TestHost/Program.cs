using System.Globalization;
using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Moving;
using Demenageur.Windows.FileSystem;

namespace Demenageur.TestHost;

/// <summary>
/// Console utilisée UNIQUEMENT par les tests de crash, sur des dossiers factices d'un bac à sable.
/// Elle exécute une opération et se tue brutalement (FailFast) à la N-ième opération disque.
/// <code>
/// move    &lt;bac&gt; &lt;opérations&gt; &lt;source&gt; &lt;destination&gt; &lt;crashAt&gt; &lt;Before|After&gt;
/// recover &lt;bac&gt; &lt;opérations&gt; &lt;crashAt&gt; &lt;Before|After&gt;
/// </code>
/// crashAt = 0 : aucun crash. Code de sortie : 0 succès, 1 échec maîtrisé, 2 usage.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "move" when args.Length == 7 => Move(args[1], args[2], args[3], args[4], int.Parse(args[5], CultureInfo.InvariantCulture), Enum.Parse<FaultMode>(args[6])),
                "recover" when args.Length == 5 => Recover(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture), Enum.Parse<FaultMode>(args[4])),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage : move <bac> <opérations> <source> <destination> <crashAt> <Before|After> | recover <bac> <opérations> <crashAt> <Before|After>");
        return 2;
    }

    private static MoveEngineContext Context(string sandbox, string operations, int crashAt, FaultMode mode)
    {
        if (!sandbox.Contains("DemenageurTests", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TestHost refuse de travailler hors d'un bac à sable de test.");
        }

        var faulty = new FaultInjectingFileSystem(new Win32FileSystem()) { Mode = mode };
        if (crashAt > 0)
        {
            faulty.FailAt.Add(crashAt);
            // Arrêt brutal, comme une coupure : aucun bloc finally, aucun rollback, pas de rapport d'erreur Windows.
            faulty.Crash = (number, what) =>
            {
                Console.Error.WriteLine($"Crash simulé n°{number} : {what}");
                Console.Error.Flush();
                NativeExit.TerminateNow(99);
            };
        }

        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var scope = new PathScopeFactory(
            protectedExact: [sandbox],
            protectedSubtrees: [PathUtil.Join(sandbox, "_canari"), windows],
            sandboxRoot: sandbox);
        using var identity = WindowsIdentity.GetCurrent();
        var store = new OperationStore(faulty, operations, [.. OperationStore.DefaultTrustedOwners(), identity.User!]);
        return new MoveEngineContext
        {
            FileSystem = faulty,
            ScopeFactory = scope,
            Store = store,
            Retry = RetryPolicy.None,
        };
    }

    private static int Move(string sandbox, string operations, string source, string destination, int crashAt, FaultMode mode)
    {
        var result = new MoveTransaction(Context(sandbox, operations, crashAt, mode)).Execute(new MoveRequest("LogicielFictif", source, destination));
        Console.WriteLine($"{result.Outcome} {result.Message}");
        return result.Succeeded ? 0 : 1;
    }

    private static int Recover(string sandbox, string operations, int crashAt, FaultMode mode)
    {
        var service = new RecoveryService(Context(sandbox, operations, crashAt, mode));
        var exit = 0;
        foreach (var operation in service.PendingOperations())
        {
            var result = service.Recover(operation);
            Console.WriteLine($"{operation.Id} {result.Outcome} {result.Message}");
            if (result.Outcome == OperationOutcome.NeedsAttention)
            {
                exit = 1;
            }
        }

        return exit;
    }
}
