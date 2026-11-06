using System.Security.Principal;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Moving;
using Demenageur.Tests.Infrastructure;
using Demenageur.TestHost;

namespace Demenageur.Tests.Moving;

public enum ObservedState
{
    /// <summary>L'original (ou la copie de retour) est un vrai dossier à l'emplacement d'origine.</summary>
    InPlace,

    /// <summary>L'emplacement d'origine est une jonction vers la destination.</summary>
    Moved,
}

/// <summary>Un faux logiciel, une destination sur un « autre disque » simulé et un dossier d'opérations, dans un bac à sable.</summary>
public sealed class MoveScenario : IDisposable
{
    public MoveScenario(bool fullSoftware = false, bool minimal = false)
    {
        Sandbox = new TestSandbox();
        Source = Sandbox.PathOf("Programmes", "LogicielFictif");
        DestinationParent = Sandbox.PathOf("Disque2", "Logiciels déplacés");
        Destination = Path.Combine(DestinationParent, "LogicielFictif");
        Operations = Sandbox.PathOf("_operations");
        Directory.CreateDirectory(DestinationParent);
        Directory.CreateDirectory(Operations);
        new FakeSoftwareBuilder(Source)
        {
            ExternalJunctionTarget = Sandbox.Canary,
            IncludeTrickyNames = fullSoftware,
            IncludeLongPath = fullSoftware,
            LargeFileSize = fullSoftware ? 3 * 1024 * 1024 + 123 : 150_000,
            Minimal = minimal,
        }.Build();
        OriginalHash = TestSandbox.HashTreeContentOnly(Source);
        Faulty = new FaultInjectingFileSystem(Sandbox.RawFs);
    }

    public TestSandbox Sandbox { get; }

    public string Source { get; }

    public string DestinationParent { get; }

    public string Destination { get; }

    public string Operations { get; }

    public string OriginalHash { get; }

    public FaultInjectingFileSystem Faulty { get; }

    public IMoveHooks Hooks { get; set; } = NoMoveHooks.Instance;

    public OperationStore Store(IFileSystem? fs = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new OperationStore(fs ?? Sandbox.RawFs, Operations, [.. OperationStore.DefaultTrustedOwners(), identity.User!]);
    }

    public MoveEngineContext Context(IFileSystem? fs = null)
    {
        fs ??= Faulty;
        return new MoveEngineContext
        {
            FileSystem = fs,
            ScopeFactory = new PathScopeFactory(
                protectedExact: [Sandbox.Root],
                protectedSubtrees: [Sandbox.Canary, Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
                sandboxRoot: Sandbox.Root),
            Store = Store(fs),
            Hooks = Hooks,
            Retry = RetryPolicy.None,
        };
    }

    public OperationResult Move(CancellationToken cancellationToken = default, Action<OperationProgress>? progress = null)
        => new MoveTransaction(Context()).Execute(new MoveRequest("LogicielFictif", Source, Destination), progress, cancellationToken);

    public OperationResult Undo(string moveOperationId, IFileSystem? fs = null)
    {
        var store = Store();
        var request = UndoRequestFactory.FromMove(store, store.Load(moveOperationId)!);
        return new UndoTransaction(Context(fs)).Execute(request);
    }

    /// <summary>Déplacements en vigueur (lecture du journal et bilan de santé).</summary>
    public IReadOnlyList<MovedSoftware> Moves() => MovedSoftwareRegistry.Load(Store(), Sandbox.RawFs);

    public OperationResult Retire(string moveOperationId, bool removeActiveJunction = false)
    {
        var moved = Moves().Single(m => m.Move.Id == moveOperationId);
        return new RetireTransaction(Context()).Execute(moved.ToRetireRequest(removeActiveJunction));
    }

    /// <summary>
    /// Imite un désinstalleur : il supprime une partie des fichiers À TRAVERS la jonction, puis la jonction elle-même
    /// (c'est ce qu'a fait le désinstalleur d'Overwolf). Le reste de la copie devient orphelin.
    /// </summary>
    public void SimulateUninstall(bool removeJunction = true)
    {
        foreach (var file in new[] { @"bin\app.exe", @"bin\core.dll" }.Select(f => Path.Combine(Source, f)))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        if (removeJunction)
        {
            Directory.Delete(Source, recursive: false); // supprime le lien seul
        }
    }

    /// <summary>Imite une mise à jour qui supprime la jonction puis recrée un vrai dossier à l'emplacement d'origine.</summary>
    public void SimulateUpdateRecreatingFolder()
    {
        Directory.Delete(Source, recursive: false);
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "nouvelle-version.txt"), "v2");
    }

    /// <summary>Reprise de toutes les opérations en attente, sans panne.</summary>
    public IReadOnlyList<OperationResult> RecoverAll()
    {
        var service = new RecoveryService(Context(Sandbox.RawFs));
        return service.PendingOperations().Select(o => service.Recover(o)).ToList();
    }

    public IReadOnlyList<StoredOperation> PendingOperations() => new RecoveryService(Context(Sandbox.RawFs)).PendingOperations();

    /// <summary>
    /// Invariant de sûreté : le logiciel est soit à sa place (vrai dossier au contenu attendu, sans copie résiduelle),
    /// soit déplacé (jonction vers une copie au contenu attendu). Le canari n'a jamais changé.
    /// </summary>
    public ObservedState AssertInvariant(string expectedHash, bool allowLeftovers = false)
    {
        Assert.True(Sandbox.CanaryIntact(), "Le canari a été modifié !");
        var atSource = Sandbox.RawFs.GetEntry(Source);
        Assert.True(atSource is not null, "L'emplacement d'origine a disparu : ni original ni jonction.");

        ObservedState state;
        if (atSource.Kind == FsEntryKind.Directory)
        {
            Assert.Equal(expectedHash, TestSandbox.HashTreeContentOnly(Source));
            if (!allowLeftovers)
            {
                Assert.False(Directory.Exists(Destination), "Copie résiduelle laissée sur la destination.");
            }

            state = ObservedState.InPlace;
        }
        else
        {
            Assert.Equal(FsEntryKind.Link, atSource.Kind);
            var target = Sandbox.RawFs.ReadReparsePoint(Source, atSource.Identity).Target;
            Assert.Equal(Destination, target);
            Assert.Equal(expectedHash, TestSandbox.HashTreeContentOnly(Destination));
            Assert.Equal(expectedHash, TestSandbox.HashTreeContentOnly(Source));
            state = ObservedState.Moved;
        }

        if (!allowLeftovers)
        {
            Assert.Empty(Leftovers());
        }

        return state;
    }

    /// <summary>
    /// Invariant minimal, valable même juste après un crash (logiciel momentanément hors ligne, verrou posé) :
    /// une copie complète au contenu attendu existe quelque part — à sa place, sous le nom verrouillé, ou dans la copie.
    /// </summary>
    public void AssertDataSafe(string expectedHash)
    {
        Assert.True(Sandbox.CanaryIntact(), "Le canari a été modifié !");
        var candidates = new List<string>();
        var atSource = Sandbox.RawFs.GetEntry(Source);
        if (atSource is { Kind: FsEntryKind.Directory })
        {
            candidates.Add(Source);
        }

        candidates.AddRange(Directory.GetDirectories(Path.GetDirectoryName(Source)!, "*.dmv-old-*"));
        if (atSource is { Kind: FsEntryKind.Link } && Directory.Exists(Destination))
        {
            candidates.Add(Destination);
        }

        Assert.True(
            candidates.Any(c => TestSandbox.HashTreeContentOnly(c) == expectedHash),
            "Aucune copie complète du logiciel n'existe plus : " + string.Join(", ", candidates));
    }

    /// <summary>Dossiers temporaires « .dmv- » restants.</summary>
    public IReadOnlyList<string> Leftovers()
        => Directory.GetDirectories(Path.GetDirectoryName(Source)!, "*.dmv-*")
            .Concat(Directory.GetDirectories(DestinationParent, "*.dmv-*"))
            .ToList();

    public void Dispose() => Sandbox.Dispose();
}
