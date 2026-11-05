using System.Security.AccessControl;
using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;
using Demenageur.Tests.Infrastructure;
using Demenageur.Tests.Inventory;
using Demenageur.Tests.Moving;
using Demenageur.Windows.Safety;

namespace Demenageur.Tests.Safety;

/// <summary>Volumes simulés : « Disque2 » est un autre volume interne NTFS (configurable).</summary>
internal sealed class StubVolumes(IFileSystem fs) : IVolumeInspector
{
    public string FileSystem { get; set; } = "NTFS";

    public DiskBusType Bus { get; set; } = DiskBusType.Internal;

    public long? Free { get; set; }

    public bool SameVolume { get; set; }

    public VolumeDetails Describe(string path)
    {
        var real = fs.GetVolumeInfo(path);
        var isDestination = path.Contains("Disque2", StringComparison.Ordinal);
        return new VolumeDetails
        {
            Volume = real with
            {
                VolumeGuidPath = isDestination && !SameVolume ? @"\\?\Volume{disque-2}\" : real.VolumeGuidPath,
                FileSystemName = isDestination ? FileSystem : real.FileSystemName,
                FreeBytesAvailable = isDestination && Free is { } free ? free : real.FreeBytesAvailable,
            },
            BusType = isDestination ? Bus : DiskBusType.Internal,
            BusDescription = isDestination ? Bus.ToString() : "interne",
        };
    }

    public IReadOnlyList<VolumeDetails> ListVolumes() => [];
}

internal sealed class NoProcesses : IProcessInspector
{
    public IReadOnlyList<ProcessUsage> FindUsers(IReadOnlyList<string> folders, IReadOnlyList<string> files) => [];
}

public sealed class PlannerTests
{
    private static (MovePlanner Planner, StubVolumes Volumes) Planner(MoveScenario scenario, IProcessInspector? processes = null)
    {
        var known = new SandboxKnownPaths(scenario.Sandbox);
        // La simulation ne fait que lire : sa garde n'autorise d'écriture que dans un dossier qui n'existe même pas.
        var fs = scenario.Sandbox.Guarded(scenario.Sandbox.PathOf("aucune-ecriture"));
        var volumes = new StubVolumes(scenario.Sandbox.RawFs);
        return (new MovePlanner(fs, known, volumes, processes ?? new NoProcesses(), () => new SystemSnapshot(), scenario.Store()), volumes);
    }

    private static MovePlanRequest Request(MoveScenario scenario)
        => new(new MoveCandidate(scenario.Source, "LogicielFictif", null, [], FolderSource.Manual), scenario.DestinationParent);

    [Fact]
    public void Dry_run_changes_nothing_and_announces_exactly_the_executed_steps()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (planner, _) = Planner(scenario);
        var before = TestSandbox.HashTree(scenario.Sandbox.Root);

        var plan = planner.Plan(Request(scenario), []);

        Assert.Equal(before, TestSandbox.HashTree(scenario.Sandbox.Root)); // la simulation ne touche à rien
        Assert.True(plan.CanExecute, string.Join("\n", plan.Verdicts.Where(v => v.Level == RuleLevel.Block).Select(v => v.Explanation)));
        Assert.Equal(scenario.Destination, plan.Destination);
        Assert.True(plan.RequiredBytes > plan.LogicalBytes);

        var executed = new List<string>();
        var result = scenario.Move(progress: p =>
        {
            if (executed.Count == 0 || executed[^1] != p.Step)
            {
                executed.Add(p.Step);
            }
        });
        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Equal(plan.Steps.Select(s => s.Step), executed);
        Assert.Equal(MoveSteps.All, executed);
    }

    [Fact]
    public void Destination_problems_are_blocking()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (planner, volumes) = Planner(scenario);

        volumes.Free = 1000;
        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.RuleId == "R-ESPACE");
        volumes.Free = null;

        volumes.FileSystem = "exFAT";
        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.Title == "Destination non NTFS");
        volumes.FileSystem = "NTFS";

        volumes.Bus = DiskBusType.Usb;
        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.Title == "Disque cible non interne");
        volumes.Bus = DiskBusType.Internal;

        volumes.SameVolume = true;
        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.Title == "Même volume");
        volumes.SameVolume = false;

        Directory.CreateDirectory(scenario.Destination);
        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.RuleId == "R-DEST-EXISTE");
    }

    [Fact]
    public void An_unsafe_existing_destination_root_is_refused()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (planner, _) = Planner(scenario);
        var security = new DirectoryInfo(scenario.DestinationParent).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(scenario.DestinationParent).SetAccessControl(security);

        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.RuleId == "R-DEST-RACINE" && v.Level == RuleLevel.Block);
    }

    [Fact]
    public void A_secure_destination_root_created_by_the_app_is_accepted()
    {
        using var scenario = new MoveScenario(minimal: true);
        var root = scenario.Sandbox.PathOf("Disque2", "Nouvelle racine");
        var context = scenario.Context(scenario.Sandbox.RawFs);
        DestinationContainer.EnsureCreated(scenario.Sandbox.RawFs, context.ScopeFactory, root);
        Assert.Null(DestinationContainer.Check(scenario.Sandbox.RawFs, root));
    }

    [Fact]
    public void Restart_manager_sees_a_file_held_open_and_the_plan_is_blocked()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (planner, _) = Planner(scenario, new RestartManagerProcessInspector());
        using (new FileStream(Path.Combine(scenario.Source, "bin", "core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var plan = planner.Plan(Request(scenario), []);
            var blocker = Assert.Single(plan.Verdicts, v => v.RuleId == "R-PROCESSUS");
            Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), blocker.Explanation, StringComparison.Ordinal);
            Assert.False(plan.CanExecute);
        }

        Assert.True(planner.Plan(Request(scenario), []).CanExecute);
    }

    [Fact]
    public void Already_moved_and_nested_folders_are_refused()
    {
        using var scenario = new MoveScenario(minimal: true);
        var (planner, _) = Planner(scenario);
        Assert.Equal(OperationOutcome.Completed, scenario.Move().Outcome);

        Assert.Contains(planner.Plan(Request(scenario), []).Verdicts, v => v.RuleId == "R-DEJA");
        var inside = new MovePlanRequest(new MoveCandidate(Path.Combine(scenario.Destination, "bin"), "x", null, [], FolderSource.Manual), scenario.Sandbox.PathOf("Disque2", "Autre"));
        Assert.Contains(planner.Plan(inside, []).Verdicts, v => v.RuleId == "R-IMBRIQUE");

        var moved = Assert.Single(MovedSoftwareRegistry.Load(scenario.Store(), scenario.Sandbox.RawFs));
        Assert.Equal(MoveHealth.Ok, moved.Health);
    }

    [Fact]
    public void Health_check_detects_a_junction_replaced_by_a_real_folder()
    {
        using var scenario = new MoveScenario(minimal: true);
        scenario.Move();
        Directory.Delete(scenario.Source);
        Directory.CreateDirectory(scenario.Source);
        Assert.Equal(MoveHealth.ReplacedByFolder, Assert.Single(MovedSoftwareRegistry.Load(scenario.Store(), scenario.Sandbox.RawFs)).Health);
    }
}
