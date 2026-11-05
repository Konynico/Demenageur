using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Moving;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.Moving;

/// <summary>
/// Vrais déplacements ENTRE DEUX VOLUMES, sur dossiers factices uniquement. Désactivés par défaut :
/// <c>$env:DEMENAGEUR_TEST_DEST='D:\DemenageurTests'</c> puis <c>.\scripts\test.ps1 -CrossVolume</c>.
/// </summary>
[Trait("Category", "CrossVolume")]
public sealed class CrossVolumeTests
{
    [Fact]
    public void Move_then_undo_between_two_real_volumes()
    {
        var destinationBase = Environment.GetEnvironmentVariable("DEMENAGEUR_TEST_DEST");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(destinationBase), "DEMENAGEUR_TEST_DEST n'est pas défini.");
        Assert.True(destinationBase!.TrimEnd('\\').EndsWith("DemenageurTests", StringComparison.OrdinalIgnoreCase),
            "Par sécurité, DEMENAGEUR_TEST_DEST doit être un dossier nommé « DemenageurTests ».");

        using var sandbox = new TestSandbox();
        var remote = Path.Combine(destinationBase, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var source = sandbox.PathOf("Programmes", "LogicielFictif");
            var destinationParent = Path.Combine(remote, "Logiciels déplacés");
            var destination = Path.Combine(destinationParent, "LogicielFictif");
            Directory.CreateDirectory(destinationParent);
            Directory.CreateDirectory(sandbox.PathOf("_operations"));
            new FakeSoftwareBuilder(source) { ExternalJunctionTarget = sandbox.Canary }.Build();
            var originalHash = TestSandbox.HashTreeContentOnly(source);

            var raw = sandbox.RawFs;
            Assert.NotEqual(raw.GetVolumeInfo(source).VolumeGuidPath, raw.GetVolumeInfo(destinationParent).VolumeGuidPath);

            using var identity = WindowsIdentity.GetCurrent();
            var context = new MoveEngineContext
            {
                FileSystem = raw,
                ScopeFactory = new PathScopeFactory(
                    protectedExact: [sandbox.Root, remote],
                    protectedSubtrees: [sandbox.Canary, Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
                    sandboxRoot: sandbox.Root,
                    additionalSandboxRoots: [remote]),
                Store = new OperationStore(raw, sandbox.PathOf("_operations"), [.. OperationStore.DefaultTrustedOwners(), identity.User!]),
                Retry = RetryPolicy.Default,
            };

            var move = new MoveTransaction(context).Execute(new MoveRequest("LogicielFictif", source, destination));
            Assert.True(move.Outcome == OperationOutcome.Completed, $"{move.Outcome} : {move.Message} {move.Error}");
            var junction = raw.GetEntry(source)!;
            Assert.Equal(FsEntryKind.Link, junction.Kind);
            Assert.Equal(destination, raw.ReadReparsePoint(source, junction.Identity).Target);
            Assert.Equal(originalHash, TestSandbox.HashTreeContentOnly(destination));
            Assert.Equal(originalHash, TestSandbox.HashTreeContentOnly(source));

            var undo = new UndoTransaction(context).Execute(UndoRequestFactory.FromMove(context.Store, context.Store.Load(move.OperationId)!));
            Assert.Equal(OperationOutcome.Completed, undo.Outcome);
            Assert.Equal(FsEntryKind.Directory, raw.GetEntry(source)!.Kind);
            Assert.Equal(originalHash, TestSandbox.HashTreeContentOnly(source));
            Assert.False(Directory.Exists(destination));
            Assert.True(sandbox.CanaryIntact());
        }
        finally
        {
            TestSandbox.DeleteForeignTestTree(remote);
        }
    }
}
