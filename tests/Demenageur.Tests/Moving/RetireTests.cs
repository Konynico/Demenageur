using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Tests.Infrastructure;
using Demenageur.TestHost;
using NSubstitute;

namespace Demenageur.Tests.Moving;

/// <summary>
/// Nettoyage d'un déplacement devenu sans objet (logiciel désinstallé, ou dossier recréé par une mise à jour) :
/// seule la copie créée par le déplacement est supprimée, jamais ce qui se trouve à l'emplacement d'origine,
/// jamais la cible d'un lien, jamais un dossier qui n'est pas le nôtre.
/// </summary>
public sealed class RetireTests
{
    private static string Moved(MoveScenario scenario)
    {
        var move = scenario.Move();
        Assert.Equal(OperationOutcome.Completed, move.Outcome);
        return move.OperationId;
    }

    [Fact]
    public void After_an_uninstall_the_leftover_copy_is_deleted_and_the_move_disappears()
    {
        using var scenario = new MoveScenario();
        var id = Moved(scenario);
        scenario.SimulateUninstall();

        var health = scenario.Moves().Single();
        Assert.Equal(MoveHealth.SourceMissing, health.Health);
        Assert.True(health.IsOrphaned);

        var result = scenario.Retire(id);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(Directory.Exists(scenario.Destination));
        Assert.False(Directory.Exists(scenario.Source));
        Assert.Empty(scenario.Moves());
        Assert.Empty(scenario.Leftovers());
        Assert.Empty(scenario.PendingOperations());
        Assert.True(scenario.Sandbox.CanaryIntact()); // la copie contenait une jonction vers le canari
    }

    [Fact]
    public void After_an_update_recreated_the_folder_only_the_old_copy_is_deleted_and_the_software_can_be_moved_again()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUpdateRecreatingFolder();
        Assert.Equal(MoveHealth.ReplacedByFolder, scenario.Moves().Single().Health);
        var newVersionHash = TestSandbox.HashTreeContentOnly(scenario.Source);

        Assert.Equal(OperationOutcome.Completed, scenario.Retire(id).Outcome);

        Assert.False(Directory.Exists(scenario.Destination));
        Assert.Equal(newVersionHash, TestSandbox.HashTreeContentOnly(scenario.Source));
        Assert.Empty(scenario.Moves());

        // La nouvelle version peut être redéplacée (la destination est libre).
        var again = scenario.Move();
        Assert.Equal(OperationOutcome.Completed, again.Outcome);
        Assert.Equal(ObservedState.Moved, scenario.AssertInvariant(newVersionHash));
        Assert.Equal(again.OperationId, scenario.Moves().Single().Move.Id);
    }

    [Fact]
    public void An_active_junction_is_never_removed_without_explicit_permission()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);

        var result = scenario.Retire(id);

        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.Equal(ObservedState.Moved, scenario.AssertInvariant(scenario.OriginalHash));
        Assert.Single(scenario.Moves());
    }

    [Fact]
    public void With_permission_the_junction_left_by_an_uninstaller_and_the_copy_are_removed()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUninstall(removeJunction: false);

        Assert.Equal(OperationOutcome.Completed, scenario.Retire(id, removeActiveJunction: true).Outcome);

        Assert.Null(scenario.Sandbox.RawFs.GetEntry(scenario.Source));
        Assert.False(Directory.Exists(scenario.Destination));
        Assert.Empty(scenario.Moves());
        Assert.Empty(scenario.Leftovers());
    }

    [Fact]
    public void A_folder_that_replaced_the_copy_is_never_deleted()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUninstall();
        TestSandbox.DeleteSandboxTree(scenario.Destination);
        Directory.CreateDirectory(scenario.Destination);
        File.WriteAllText(Path.Combine(scenario.Destination, "autre.txt"), "pas à nous");

        var result = scenario.Retire(id);

        Assert.Equal(OperationOutcome.RolledBack, result.Outcome);
        Assert.True(File.Exists(Path.Combine(scenario.Destination, "autre.txt")));
        Assert.Single(scenario.Moves());
    }

    [Fact]
    public void An_open_file_in_the_copy_blocks_the_cleanup_without_deleting_anything()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUninstall();
        var before = TestSandbox.HashTreeContentOnly(scenario.Destination);
        var openFile = Directory.GetFiles(scenario.Destination, "*", SearchOption.TopDirectoryOnly).First();

        using (new FileStream(openFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(OperationOutcome.RolledBack, scenario.Retire(id).Outcome);
        }

        Assert.Equal(before, TestSandbox.HashTreeContentOnly(scenario.Destination));
        Assert.Empty(scenario.Leftovers());
        Assert.Empty(scenario.PendingOperations());

        // Une fois le fichier fermé, le nettoyage passe.
        Assert.Equal(OperationOutcome.Completed, scenario.Retire(id).Outcome);
        Assert.False(Directory.Exists(scenario.Destination));
    }

    [Fact]
    public void When_nothing_is_left_the_move_is_simply_removed_from_the_list()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUninstall();
        TestSandbox.DeleteSandboxTree(scenario.Destination);

        Assert.Equal(OperationOutcome.Completed, scenario.Retire(id).Outcome);
        Assert.Empty(scenario.Moves());
    }

    [Fact]
    public void A_copy_missing_behind_an_active_junction_is_not_cleaned_up_unplugged_disk()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        Directory.Move(scenario.Destination, scenario.Destination + "-debranche");

        Assert.Equal(OperationOutcome.RolledBack, scenario.Retire(id, removeActiveJunction: true).Outcome);
        Assert.Equal(FsEntryKind.Link, scenario.Sandbox.RawFs.GetEntry(scenario.Source)!.Kind);

        Directory.Move(scenario.Destination + "-debranche", scenario.Destination);
        scenario.AssertInvariant(scenario.OriginalHash);
    }

    [Fact]
    public void A_crash_after_the_point_of_no_return_is_finished_by_the_recovery()
    {
        using var scenario = new MoveScenario(minimal: true);
        var id = Moved(scenario);
        scenario.SimulateUninstall();

        // Le premier Delete échoue juste après « Committed » : la suppression reste à faire.
        scenario.Faulty.BeforeMutation = (operation, _) =>
        {
            if (operation == "Delete" && scenario.Faulty.FailAt.Count == 0)
            {
                scenario.Faulty.FailAt.Add(scenario.Faulty.MutationCount + 1);
            }
        };
        var first = scenario.Retire(id);
        scenario.Faulty.BeforeMutation = null;
        scenario.Faulty.FailAt.Clear();

        Assert.Equal(OperationOutcome.CompletedWithPendingCleanup, first.Outcome);
        Assert.Empty(scenario.Moves()); // le nettoyage est décidé : le déplacement n'est plus listé
        Assert.Single(scenario.PendingOperations());

        Assert.All(scenario.RecoverAll(), r => Assert.Equal(OperationOutcome.Completed, r.Outcome));
        Assert.False(Directory.Exists(scenario.Destination));
        Assert.Empty(scenario.Leftovers());
        Assert.Empty(scenario.PendingOperations());
    }

    private static int CountRetireMutations(bool junctionLeftInPlace)
    {
        using var probe = new MoveScenario(minimal: true);
        var id = Moved(probe);
        probe.SimulateUninstall(removeJunction: !junctionLeftInPlace);
        var before = probe.Faulty.MutationCount;
        Assert.Equal(OperationOutcome.Completed, probe.Retire(id, removeActiveJunction: junctionLeftInPlace).Outcome);
        return probe.Faulty.MutationCount - before;
    }

    /// <summary>
    /// Une panne à CHAQUE opération du nettoyage, puis la reprise : soit rien n'a été supprimé (copie intacte,
    /// jonction remise si elle était là), soit tout est supprimé ; jamais d'état intermédiaire durable ni de résidu.
    /// </summary>
    [Theory]
    [Trait("Category", "Exhaustif")]
    [InlineData(FaultMode.Before, false)]
    [InlineData(FaultMode.After, false)]
    [InlineData(FaultMode.Before, true)]
    [InlineData(FaultMode.After, true)]
    public void Every_single_fault_during_a_cleanup_is_safe(FaultMode mode, bool junctionLeftInPlace)
    {
        FaultInjectionTests.RunForEach(CountRetireMutations(junctionLeftInPlace), n =>
        {
            using var scenario = new MoveScenario(minimal: true);
            var id = Moved(scenario);
            scenario.SimulateUninstall(removeJunction: !junctionLeftInPlace);
            var copyHash = TestSandbox.HashTreeContentOnly(scenario.Destination);
            scenario.Faulty.Mode = mode;
            scenario.Faulty.FailAt.Add(scenario.Faulty.MutationCount + n);

            var result = scenario.Retire(id, removeActiveJunction: junctionLeftInPlace);
            Assert.True(scenario.Sandbox.CanaryIntact());
            scenario.Faulty.FailAt.Clear();
            foreach (var recovery in scenario.RecoverAll())
            {
                Assert.True(recovery.Outcome != OperationOutcome.NeedsAttention, $"{result.Outcome} puis reprise : {recovery.Message}");
            }

            Assert.Empty(scenario.Leftovers());
            Assert.Empty(scenario.PendingOperations());
            var atSource = scenario.Sandbox.RawFs.GetEntry(scenario.Source);
            if (Directory.Exists(scenario.Destination))
            {
                // Rien n'a été supprimé : copie intacte, toujours listée, jonction comme avant.
                Assert.Equal(copyHash, TestSandbox.HashTreeContentOnly(scenario.Destination));
                Assert.Single(scenario.Moves());
                Assert.Equal(junctionLeftInPlace, atSource is { Kind: FsEntryKind.Link });
            }
            else
            {
                Assert.Empty(scenario.Moves());
                Assert.Null(atSource);
            }
        });
    }

    // ── Simulation du nettoyage (planificateur) ──

    private static RetirePlan Plan(MoveScenario scenario, params UninstallEntry[] registrations)
    {
        var processes = Substitute.For<IProcessInspector>();
        processes.FindUsers(default!, default!).ReturnsForAnyArgs([]);
        return new RetirePlanner(scenario.Sandbox.RawFs, processes).Plan(scenario.Moves().Single(), registrations);
    }

    private static UninstallEntry Entry(string name, string? location = null, string? icon = null)
        => new() { RegistryKey = "HKLM\\X\\" + name, Scope = RegistryScope.Machine64, DisplayName = name, InstallLocation = location, DisplayIcon = icon };

    [Fact]
    public void Planner_blocks_while_the_software_is_still_registered_behind_its_junction()
    {
        using var scenario = new MoveScenario(minimal: true);
        Moved(scenario);

        var plan = Plan(scenario, Entry("LogicielFictif", icon: $"\"{Path.Combine(scenario.Source, "bin", "app.exe")}\",0"));

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Verdicts, v => v.RuleId == "N-INSTALLE" && v.Level == RuleLevel.Block);
    }

    [Fact]
    public void Planner_allows_a_junction_left_by_an_uninstaller_with_strong_confirmation()
    {
        using var scenario = new MoveScenario(minimal: true);
        Moved(scenario);
        scenario.SimulateUninstall(removeJunction: false);

        var plan = Plan(scenario);

        Assert.True(plan.CanExecute);
        Assert.True(plan.RemoveActiveJunction);
        Assert.True(plan.RequiresStrongConfirmation);
        Assert.Contains(plan.Steps, s => s.Step == RetireSteps.RemoveJunction);
    }

    [Fact]
    public void Planner_ignores_registrations_once_an_update_recreated_the_folder()
    {
        using var scenario = new MoveScenario(minimal: true);
        Moved(scenario);
        scenario.SimulateUpdateRecreatingFolder();

        var plan = Plan(scenario, Entry("LogicielFictif", location: scenario.Source));

        Assert.True(plan.CanExecute);
        Assert.False(plan.RemoveActiveJunction);
        Assert.False(plan.RequiresStrongConfirmation);
        Assert.True(plan.CopyFiles > 0);
        Assert.DoesNotContain(plan.Steps, s => s.Step == RetireSteps.RemoveJunction);
    }

    [Fact]
    public void Planner_blocks_when_the_copy_is_in_use()
    {
        using var scenario = new MoveScenario(minimal: true);
        Moved(scenario);
        scenario.SimulateUninstall();
        var processes = Substitute.For<IProcessInspector>();
        processes.FindUsers(default!, default!).ReturnsForAnyArgs([new ProcessUsage(42, "Overwolf.exe", null, null, false, "utilise la copie")]);

        var plan = new RetirePlanner(scenario.Sandbox.RawFs, processes).Plan(scenario.Moves().Single(), []);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Verdicts, v => v.RuleId == "N-PROCESSUS");
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Overwolf", null, null, null, false)]
    [InlineData(@"C:\Program Files (x86)\Overwolf", @"C:\Program Files (x86)\Overwolf", null, null, true)]
    [InlineData(@"C:\Program Files (x86)\Overwolf", null, "\"C:\\Program Files (x86)\\Overwolf\\OWUninstaller.exe\" /S", null, true)]
    [InlineData(@"C:\Program Files (x86)\Overwolf", null, null, @"C:\Program Files (x86)\Overwolf\Overwolf.exe,0", true)]
    [InlineData(@"C:\Program Files (x86)\Overwolf", @"C:\Program Files (x86)\Overwolf Extra", null, null, false)]
    [InlineData(@"C:\Program Files (x86)\Overwolf", null, "MsiExec.exe /X{1234}", null, false)]
    public void Uninstall_references_are_detected_by_folder(string folder, string? location, string? uninstall, string? icon, bool expected)
    {
        var entry = new UninstallEntry { RegistryKey = "k", Scope = RegistryScope.Machine32, DisplayName = "Overwolf", InstallLocation = location, UninstallString = uninstall, DisplayIcon = icon };
        Assert.Equal(expected, UninstallReferences.References(entry, folder));
    }
}
