using System.Diagnostics;
using Demenageur.Core.Moving;

namespace Demenageur.Tests.Moving;

/// <summary>
/// Vrais crashs : un processus séparé (TestHost) exécute le déplacement et se termine brutalement à la N-ième
/// opération disque, pour chaque N. La reprise est lancée dans un nouveau processus, qui est lui-même tué en cours
/// de route, puis relancée jusqu'au bout. L'invariant doit tenir à chaque arrêt.
/// </summary>
[Trait("Category", "Exhaustif")]
public sealed class CrashTests
{
    private static readonly string HostDll = Path.Combine(AppContext.BaseDirectory, "Demenageur.TestHost.dll");

    private static (int ExitCode, string Output) Host(params object[] arguments)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(HostDll);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(Convert.ToString(argument, System.Globalization.CultureInfo.InvariantCulture)!);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("TestHost ne répond plus.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void Killing_the_process_at_every_step_and_during_recovery_is_safe()
    {
        Assert.True(File.Exists(HostDll), "TestHost introuvable : " + HostDll);
        var total = FaultInjectionTests.CountMoveMutations();

        FaultInjectionTests.RunForEach(total, n =>
        {
            using var scenario = new MoveScenario(minimal: true);
            var (moveExit, moveOutput) = Host("move", scenario.Sandbox.Root, scenario.Operations, scenario.Source, scenario.Destination, n, "Before");
            Assert.True(moveExit == 99, $"Le processus aurait dû être tué à l'opération {n} : {moveOutput}");

            // Juste après le crash, le logiciel peut être hors ligne (verrou posé) mais une copie complète existe.
            scenario.AssertDataSafe(scenario.OriginalHash);

            // La reprise elle-même est tuée (sauf s'il n'y a rien à reprendre).
            Host("recover", scenario.Sandbox.Root, scenario.Operations, 2 + (n % 3), "After");
            scenario.AssertDataSafe(scenario.OriginalHash);

            var (recoverExit, recoverOutput) = Host("recover", scenario.Sandbox.Root, scenario.Operations, 0, "Before");
            Assert.True(recoverExit == 0, $"Reprise finale en échec : {recoverOutput}");
            scenario.AssertInvariant(scenario.OriginalHash);
            Assert.Empty(scenario.PendingOperations());
        });
    }

    [Fact]
    public void A_move_without_crash_completes_in_a_separate_process()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (exit, output) = Host("move", scenario.Sandbox.Root, scenario.Operations, scenario.Source, scenario.Destination, 0, "Before");
        Assert.True(exit == 0, output);
        Assert.Equal(ObservedState.Moved, scenario.AssertInvariant(scenario.OriginalHash));
        Assert.Contains(nameof(OperationOutcome.Completed), output, StringComparison.Ordinal);
    }
}
