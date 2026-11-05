using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.Copy;

/// <summary>Prépare un faux logiciel, son manifeste et une destination privée vide, dans un bac à sable.</summary>
public sealed class CopyScenario : IDisposable
{
    public CopyScenario(bool fullSoftware = true)
    {
        Sandbox = new TestSandbox();
        Source = Sandbox.PathOf("source", "LogicielFictif");
        Destination = Sandbox.PathOf("destination", "LogicielFictif");
        Directory.CreateDirectory(Sandbox.PathOf("destination"));
        Software = new FakeSoftwareBuilder(Source)
        {
            ExternalJunctionTarget = Sandbox.Canary,
            IncludeLongPath = fullSoftware,
            IncludeTrickyNames = fullSoftware,
        }.Build();
        Fs = Sandbox.Guarded(Sandbox.PathOf("source"), Sandbox.PathOf("destination"));
    }

    public TestSandbox Sandbox { get; }

    public string Source { get; }

    public string Destination { get; }

    public FakeSoftware Software { get; }

    public PathGuard Fs { get; }

    public Manifest? Manifest { get; private set; }

    public FileIdentity DestinationIdentity { get; private set; }

    public Manifest Scan()
    {
        var snapshot = new TreeScanner(Fs).Scan(Source, ScanOptions.Full);
        Assert.False(snapshot.HasBlockingProblems, string.Join("\n", snapshot.Problems.Select(p => p.Message)));
        Manifest = Core.Copy.Manifest.FromSnapshot(snapshot);
        return Manifest;
    }

    public TreeCopier CopyAll(CancellationToken cancellationToken = default)
    {
        var manifest = Manifest ?? Scan();
        DestinationIdentity = Fs.CreateDirectory(Destination, SecurityDescriptorTools.CreatePrivateDirectoryDescriptor());
        var copier = new TreeCopier(Fs, RetryPolicy.None);
        copier.Copy(manifest, Destination, DestinationIdentity, cancellationToken: cancellationToken);
        return copier;
    }

    public VerificationResult Verify(VerificationOptions? options = null)
        => new TreeVerifier(Fs).Verify(Manifest!, Destination, options);

    public string Dest(string relative) => Path.Combine(Destination, relative);

    public void Dispose() => Sandbox.Dispose();
}
