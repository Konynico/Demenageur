using Demenageur.Core.Moving;

namespace Demenageur.Tests.Moving;

public sealed class OperationHistoryTests
{
    [Fact]
    public void Only_settled_operations_are_erasable_and_active_moves_are_always_kept()
    {
        using var scenario = new MoveScenario();

        scenario.Hooks = new RefusingHooks();
        var abandoned = scenario.Move();
        Assert.Equal(OperationOutcome.RolledBack, abandoned.Outcome);

        scenario.Hooks = NoMoveHooks.Instance;
        var first = scenario.Move();
        Assert.True(first.Succeeded);
        var undo = scenario.Undo(first.OperationId);
        Assert.Equal(OperationOutcome.Completed, undo.Outcome);
        var active = scenario.Move();
        Assert.True(active.Succeeded);

        var store = scenario.Store();
        var deletable = OperationHistory.FindDeletable(store.List()).Select(o => o.Id).ToList();

        Assert.Equal(new[] { abandoned.OperationId, first.OperationId, undo.OperationId }.Order(StringComparer.Ordinal), deletable);
        Assert.DoesNotContain(active.OperationId, deletable);

        var guard = scenario.Sandbox.Guarded(scenario.Operations);
        foreach (var operation in OperationHistory.FindDeletable(store.List()))
        {
            Assert.True(OperationHistory.Delete(guard, operation));
        }

        var remaining = Assert.Single(store.List());
        Assert.Equal(active.OperationId, remaining.Id);
        var moved = Assert.Single(MovedSoftwareRegistry.Load(store, scenario.Sandbox.RawFs));
        Assert.Equal(MoveHealth.Ok, moved.Health);
        Assert.Empty(OperationHistory.FindDeletable(store.List()));
        Assert.True(scenario.Sandbox.CanaryIntact());

        // Le déplacement restant s'annule toujours normalement.
        Assert.Equal(OperationOutcome.Completed, scenario.Undo(active.OperationId).Outcome);
    }

    private sealed class RefusingHooks : IMoveHooks
    {
        public string? CheckBeforeLock(string folderToLock) => "Logiciel ouvert.";

        public void EnterCriticalSection()
        {
        }

        public void LeaveCriticalSection()
        {
        }
    }
}
