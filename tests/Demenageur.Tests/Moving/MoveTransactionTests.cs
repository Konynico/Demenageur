using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;
using Demenageur.Core.Moving;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.Moving;

public sealed class MoveTransactionTests
{
    [Fact]
    public void Move_replaces_the_folder_by_a_junction_to_a_verified_copy()
    {
        using var scenario = new MoveScenario(fullSoftware: true);
        var originalRoot = scenario.Sandbox.RawFs.GetEntry(scenario.Source)!;
        var steps = new List<string>();

        var result = scenario.Move(progress: p => steps.Add(p.Step));

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Equal(ObservedState.Moved, scenario.AssertInvariant(scenario.OriginalHash));
        Assert.Contains("Validation de la jonction", steps);

        // La jonction reprend les dates du dossier d'origine.
        var junction = scenario.Sandbox.RawFs.GetEntry(scenario.Source)!;
        Assert.Equal(originalRoot.Times.CreationTime, junction.Times.CreationTime);

        // La racine de la copie porte les droits effectifs de l'original, protégés.
        var store = scenario.Store();
        var operation = Assert.Single(store.List());
        Assert.True(operation.IsCompleted);
        var manifest = store.LoadManifest(operation.Id, OperationStore.ManifestFileName)!;
        var destinationRoot = scenario.Sandbox.RawFs.GetEntry(scenario.Destination)!;
        var rootSd = scenario.Sandbox.RawFs.GetSecurityDescriptor(scenario.Destination, destinationRoot.Identity);
        Assert.True(SecurityDescriptorTools.IsDaclProtected(rootSd));
        Assert.True(SecurityDescriptorTools.EffectiveDaclEquals(manifest.Root.SecurityDescriptor!, rootSd));
        Assert.Empty(scenario.PendingOperations());
    }

    [Fact]
    public void Undo_brings_everything_back_including_changes_made_after_the_move()
    {
        using var scenario = new MoveScenario(fullSoftware: true);
        var move = scenario.Move();
        Assert.Equal(OperationOutcome.Completed, move.Outcome);

        // Le logiciel « se met à jour » à travers la jonction.
        File.WriteAllText(Path.Combine(scenario.Source, "config", "settings.json"), "{ \"langue\": \"en\" }");
        File.WriteAllText(Path.Combine(scenario.Source, "bin", "nouveau-module.dll"), "mise à jour");
        var expected = TestSandbox.HashTreeContentOnly(scenario.Source);

        var undo = scenario.Undo(move.OperationId);

        Assert.Equal(OperationOutcome.Completed, undo.Outcome);
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(expected));
        var store = scenario.Store();
        var manifest = store.LoadManifest(move.OperationId, OperationStore.ManifestFileName)!;
        var root = scenario.Sandbox.RawFs.GetEntry(scenario.Source)!;
        Assert.True(SecurityDescriptorTools.DaclEquals(manifest.Root.SecurityDescriptor!, scenario.Sandbox.RawFs.GetSecurityDescriptor(scenario.Source, root.Identity)));
        Assert.Equal("{ \"langue\": \"en\" }", File.ReadAllText(Path.Combine(scenario.Source, "config", "settings.json")));
        Assert.Empty(scenario.PendingOperations());
    }

    [Fact]
    public void An_open_file_prevents_the_lock_and_nothing_changes()
    {
        using var scenario = new MoveScenario();
        using (new FileStream(Path.Combine(scenario.Source, "bin", "core.dll"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = scenario.Move();
            Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
            Assert.Contains("encore ouvert", result.Message, StringComparison.Ordinal);
        }

        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
        Assert.Empty(scenario.PendingOperations());
    }

    [Fact]
    public void A_process_working_in_the_folder_prevents_the_lock()
    {
        using var scenario = new MoveScenario();
        using var process = TestInterop.StartProcessWithCurrentDirectory(Path.Combine(scenario.Source, "bin"));
        try
        {
            Thread.Sleep(300);
            Assert.Equal(OperationOutcome.RolledBack, scenario.Move().Outcome);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void Last_moment_process_check_can_refuse_the_move()
    {
        using var scenario = new MoveScenario();
        scenario.Hooks = new RefusingHooks("LogicielFictif.exe est en cours d'exécution.");
        var result = scenario.Move();
        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.Contains("en cours d'exécution", result.Message, StringComparison.Ordinal);
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void An_existing_destination_is_never_touched()
    {
        using var scenario = new MoveScenario();
        Directory.CreateDirectory(scenario.Destination);
        File.WriteAllText(Path.Combine(scenario.Destination, "données-de-quelqu-un.txt"), "à garder");

        var result = scenario.Move();

        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.Equal("à garder", File.ReadAllText(Path.Combine(scenario.Destination, "données-de-quelqu-un.txt")));
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash, allowLeftovers: true));
        Assert.Empty(scenario.Leftovers());
    }

    [Fact]
    public void Cancelling_during_the_copy_rolls_back()
    {
        using var scenario = new MoveScenario();
        using var cts = new CancellationTokenSource();
        var result = scenario.Move(cts.Token, p =>
        {
            if (p.Transfer is { ItemsDone: > 4 })
            {
                cts.Cancel();
            }
        });

        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void Corruption_of_the_copy_before_verification_rolls_back()
    {
        using var scenario = new MoveScenario();
        var tampered = false;
        scenario.Faulty.BeforeMutation = (operation, path) =>
        {
            // Premier « SetBasicInfo » : toutes les données sont copiées, la vérification n'a pas encore eu lieu.
            if (!tampered && operation == nameof(IFileSystem.SetBasicInfo) && path.StartsWith(scenario.Destination, StringComparison.Ordinal))
            {
                tampered = true;
                using var stream = new FileStream(Path.Combine(scenario.Destination, "bin", "core.dll"), FileMode.Open, FileAccess.ReadWrite);
                stream.Position = 1234;
                stream.WriteByte(0x42);
            }
        };

        var result = scenario.Move();

        Assert.True(tampered);
        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.Contains("Contenu différent", result.Message, StringComparison.Ordinal);
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void A_read_error_on_the_source_rolls_back()
    {
        using var scenario = new MoveScenario();
        scenario.Faulty.FailHashAt = 3;
        Assert.Equal(OperationOutcome.RolledBack, scenario.Move().Outcome);
        Assert.Equal(ObservedState.InPlace, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void If_someone_recreates_the_original_folder_nothing_is_deleted()
    {
        using var scenario = new MoveScenario();
        scenario.Faulty.BeforeMutation = (operation, path) =>
        {
            if (operation == nameof(IFileSystem.CreateReparsePoint) && path == scenario.Source)
            {
                Directory.CreateDirectory(scenario.Source);
                File.WriteAllText(Path.Combine(scenario.Source, "recree-par-un-tiers.txt"), "x");
            }
        };

        var result = scenario.Move();

        Assert.Equal(OperationOutcome.NeedsAttention, result.Outcome);
        Assert.Equal("x", File.ReadAllText(Path.Combine(scenario.Source, "recree-par-un-tiers.txt")));
        var locked = Assert.Single(scenario.Leftovers(), l => l.Contains("Programmes", StringComparison.Ordinal));
        Assert.Equal(scenario.OriginalHash, TestSandbox.HashTreeContentOnly(locked)); // l'original est intact
        Assert.True(Directory.Exists(scenario.Destination)); // la copie vérifiée aussi
        Assert.True(scenario.Sandbox.CanaryIntact());
    }

    [Fact]
    public void Undo_is_refused_if_the_junction_was_replaced_by_a_real_folder()
    {
        using var scenario = new MoveScenario();
        var move = scenario.Move();
        Assert.True(move.Succeeded);

        // Un programme de mise à jour supprime la jonction et réinstalle sur C:.
        Directory.Delete(scenario.Source);
        Directory.CreateDirectory(scenario.Source);
        File.WriteAllText(Path.Combine(scenario.Source, "reinstalle.txt"), "y");
        var destinationHash = TestSandbox.HashTreeContentOnly(scenario.Destination);

        var undo = scenario.Undo(move.OperationId);

        Assert.Equal(OperationOutcome.RolledBack, undo.Outcome);
        Assert.Equal("y", File.ReadAllText(Path.Combine(scenario.Source, "reinstalle.txt")));
        Assert.Equal(destinationHash, TestSandbox.HashTreeContentOnly(scenario.Destination));
    }

    [Fact]
    public void Undo_is_refused_while_the_moved_software_is_in_use()
    {
        using var scenario = new MoveScenario();
        var move = scenario.Move();
        using (new FileStream(Path.Combine(scenario.Source, "bin", "app.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var undo = scenario.Undo(move.OperationId);
            Assert.Equal(OperationOutcome.RolledBack, undo.Outcome);
        }

        Assert.Equal(ObservedState.Moved, scenario.AssertInvariant(scenario.OriginalHash));
    }

    [Fact]
    public void An_untrusted_journal_is_ignored()
    {
        using var scenario = new MoveScenario();
        scenario.Faulty.FailAt.Add(12); // interruption pour laisser une opération en attente
        scenario.Move();
        scenario.Faulty.FailAt.Clear();

        // Magasin qui ne fait confiance qu'aux administrateurs : le journal (propriété de l'utilisateur de test) est ignoré.
        var strict = new OperationStore(scenario.Sandbox.RawFs, scenario.Operations);
        var operation = Assert.Single(strict.List());
        Assert.False(operation.Trusted);

        // Manifeste : même règle que le journal. Un manifeste forgé ne doit jamais guider une suppression.
        if (scenario.Store().LoadManifest(operation.Id, OperationStore.ManifestFileName) is not null)
        {
            Assert.Null(strict.LoadManifest(operation.Id, OperationStore.ManifestFileName));
        }

        var context = scenario.Context(scenario.Sandbox.RawFs);
        var service = new RecoveryService(new MoveEngineContext { FileSystem = context.FileSystem, ScopeFactory = context.ScopeFactory, Store = strict, Retry = RetryPolicy.None });
        Assert.Equal(OperationOutcome.NeedsAttention, service.Recover(operation).Outcome);
    }

    private sealed class RefusingHooks(string reason) : IMoveHooks
    {
        public string? CheckBeforeLock(string folderToLock) => reason;

        public void EnterCriticalSection()
        {
        }

        public void LeaveCriticalSection()
        {
        }
    }
}
