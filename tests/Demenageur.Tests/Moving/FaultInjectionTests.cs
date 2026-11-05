using System.Collections.Concurrent;
using Demenageur.Core.Moving;
using Demenageur.TestHost;

namespace Demenageur.Tests.Moving;

/// <summary>
/// Injection exhaustive de pannes : la N-ième opération qui modifie le disque échoue, pour CHAQUE N.
/// Après chaque panne puis après la reprise, l'invariant doit tenir : le logiciel est à sa place OU déplacé,
/// toujours complet, jamais perdu ; le canari n'a jamais changé ; aucun résidu une fois la reprise faite.
/// Les scénarios (un bac à sable chacun) s'exécutent en parallèle.
/// </summary>
[Trait("Category", "Exhaustif")]
public sealed class FaultInjectionTests
{
    private static readonly int Parallelism = Math.Max(2, Environment.ProcessorCount / 2);

    internal static int CountMoveMutations()
    {
        using var scenario = new MoveScenario(minimal: true);
        Assert.Equal(OperationOutcome.Completed, scenario.Move().Outcome);
        return scenario.Faulty.MutationCount;
    }

    internal static void RunForEach(int total, Action<int> scenario)
    {
        var failures = new ConcurrentBag<string>();
        Parallel.For(1, total + 1, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, n =>
        {
            try
            {
                scenario(n);
            }
            catch (Exception ex)
            {
                failures.Add($"n°{n} : {ex.Message}");
            }
        });

        Assert.True(failures.IsEmpty, $"{failures.Count}/{total} cas en échec :\n" + string.Join("\n", failures.OrderBy(f => f, StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData(FaultMode.Before)]
    [InlineData(FaultMode.After)]
    public void Every_single_fault_during_a_move_is_safe(FaultMode mode)
    {
        RunForEach(CountMoveMutations(), n =>
        {
            using var scenario = new MoveScenario(minimal: true);
            scenario.Faulty.Mode = mode;
            scenario.Faulty.FailAt.Add(n);
            var result = scenario.Move();
            var context = $"[{scenario.Faulty.Log.ElementAtOrDefault(n - 1)}] ";
            try
            {
                scenario.AssertInvariant(scenario.OriginalHash, allowLeftovers: true);
                scenario.Faulty.FailAt.Clear();
                foreach (var recovery in scenario.RecoverAll())
                {
                    Assert.True(recovery.Outcome != OperationOutcome.NeedsAttention, "Reprise : " + recovery.Message);
                }

                var state = scenario.AssertInvariant(scenario.OriginalHash);
                Assert.Empty(scenario.PendingOperations());
                if (result.Outcome == OperationOutcome.RolledBack)
                {
                    Assert.Equal(ObservedState.InPlace, state);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(context + result.Outcome + " — " + ex.Message, ex);
            }
        });
    }

    [Fact]
    public void A_second_fault_during_the_rollback_is_still_safe()
    {
        RunForEach(CountMoveMutations(), n =>
        {
            using var scenario = new MoveScenario(minimal: true);
            scenario.Faulty.FailAt.Add(n);
            scenario.Faulty.FailAt.Add(n + 2); // panne pendant le retour en arrière
            scenario.Move();
            scenario.AssertInvariant(scenario.OriginalHash, allowLeftovers: true);
            scenario.Faulty.FailAt.Clear();
            scenario.RecoverAll();
            scenario.AssertInvariant(scenario.OriginalHash);
            Assert.Empty(scenario.PendingOperations());
        });
    }

    [Theory]
    [InlineData(FaultMode.Before)]
    [InlineData(FaultMode.After)]
    public void Every_single_fault_during_an_undo_is_safe(FaultMode mode)
    {
        int total;
        using (var probe = new MoveScenario(minimal: true))
        {
            Assert.True(probe.Move().Succeeded);
            var moveMutations = probe.Faulty.MutationCount;
            Assert.Equal(OperationOutcome.Completed, probe.Undo(probe.Store().List().Single().Id).Outcome);
            total = probe.Faulty.MutationCount - moveMutations;
        }

        RunForEach(total, n =>
        {
            using var scenario = new MoveScenario(minimal: true);
            var move = scenario.Move();
            Assert.True(move.Succeeded);
            scenario.Faulty.Mode = mode;
            scenario.Faulty.FailAt.Add(scenario.Faulty.MutationCount + n);
            var undo = scenario.Undo(move.OperationId);
            scenario.AssertInvariant(scenario.OriginalHash, allowLeftovers: true);
            scenario.Faulty.FailAt.Clear();
            foreach (var recovery in scenario.RecoverAll())
            {
                Assert.True(recovery.Outcome != OperationOutcome.NeedsAttention, "Reprise : " + recovery.Message);
            }

            var state = scenario.AssertInvariant(scenario.OriginalHash);
            Assert.Empty(scenario.PendingOperations());
            if (undo.Outcome == OperationOutcome.RolledBack)
            {
                Assert.Equal(ObservedState.Moved, state);
            }
        });
    }
}
