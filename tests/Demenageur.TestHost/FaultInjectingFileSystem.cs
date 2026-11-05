using Demenageur.Core.FileSystem;

namespace Demenageur.TestHost;

/// <summary>Quand injecter la panne par rapport à l'opération visée.</summary>
public enum FaultMode
{
    /// <summary>L'opération n'est pas exécutée.</summary>
    Before,

    /// <summary>L'opération est exécutée, puis une erreur est signalée (résultat ambigu).</summary>
    After,
}

/// <summary>
/// Décorateur de test : compte les opérations qui modifient le disque et fait échouer (ou tue le processus à)
/// la N-ième. Permet de vérifier l'invariant de sûreté après une panne à CHAQUE étape possible.
/// </summary>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private static readonly FsErrorKind[] Kinds = [FsErrorKind.DiskFull, FsErrorKind.SharingViolation, FsErrorKind.AccessDenied, FsErrorKind.Other];

    private readonly IFileSystem _inner;

    public FaultInjectingFileSystem(IFileSystem inner)
    {
        _inner = inner;
    }

    /// <summary>Numéros (à partir de 1) des opérations qui échouent.</summary>
    public HashSet<int> FailAt { get; } = [];

    public FaultMode Mode { get; set; } = FaultMode.Before;

    /// <summary>Si défini, appelé à la place de l'erreur (ex. : tuer le processus).</summary>
    public Action<int, string>? Crash { get; set; }

    /// <summary>Appelé avant chaque opération modifiante (nom, chemin) : permet de simuler un tiers.</summary>
    public Action<string, string>? BeforeMutation { get; set; }

    /// <summary>Numéro (à partir de 1) de la lecture d'empreinte qui échoue (erreur CRC).</summary>
    public int? FailHashAt { get; set; }

    public int MutationCount { get; private set; }

    public int HashCount { get; private set; }

    public List<string> Log { get; } = [];

    private T Mutate<T>(string operation, string path, Func<T> action)
    {
        BeforeMutation?.Invoke(operation, path);
        var number = ++MutationCount;
        Log.Add($"{number} {operation} {path}");
        var fail = FailAt.Contains(number);
        if (fail && Mode == FaultMode.Before)
        {
            Fail(number, operation, path);
        }

        var result = action();
        if (fail && Mode == FaultMode.After)
        {
            Fail(number, operation, path);
        }

        return result;
    }

    private void Mutate(string operation, string path, Action action)
        => Mutate<object?>(operation, path, () =>
        {
            action();
            return null;
        });

    private void Fail(int number, string operation, string path)
    {
        if (Crash is not null)
        {
            Crash(number, $"{operation} {path}");
        }

        var kind = Kinds[number % Kinds.Length];
        throw new FileSystemException(kind, $"Panne injectée n°{number} ({kind}) sur {operation} « {path} ».", path);
    }

    // ───── Lecture ─────
    public FsEntryInfo? GetEntry(string path) => _inner.GetEntry(path);

    public FsEntryInfo? GetEntryFollowingLinks(string path) => _inner.GetEntryFollowingLinks(path);

    public IReadOnlyList<DirectoryChild> ListDirectory(string directoryPath, FileIdentity expectedIdentity, bool followLink = false)
        => _inner.ListDirectory(directoryPath, expectedIdentity, followLink);

    public ReparsePointData ReadReparsePoint(string path, FileIdentity expectedIdentity) => _inner.ReadReparsePoint(path, expectedIdentity);

    public IReadOnlyList<AlternateStream> ListAlternateStreams(string filePath, FileIdentity expectedIdentity) => _inner.ListAlternateStreams(filePath, expectedIdentity);

    public ContentHash HashStream(string filePath, string? streamName, FileIdentity expectedIdentity, bool unbuffered, Action<long>? bytesRead, CancellationToken cancellationToken)
    {
        if (++HashCount == FailHashAt)
        {
            throw new FileSystemException(FsErrorKind.DataError, $"Erreur de lecture injectée (CRC) sur « {filePath} ».", filePath);
        }

        return _inner.HashStream(filePath, streamName, expectedIdentity, unbuffered, bytesRead, cancellationToken);
    }

    public byte[] GetSecurityDescriptor(string path, FileIdentity expectedIdentity) => _inner.GetSecurityDescriptor(path, expectedIdentity);

    public string GetFinalPath(string path, bool followFinalLink) => _inner.GetFinalPath(path, followFinalLink);

    public VolumeInfo GetVolumeInfo(string path) => _inner.GetVolumeInfo(path);

    public byte[]? ReadAllBytes(string path) => _inner.ReadAllBytes(path);

    public void ProbeRead(string filePath, FileIdentity expectedIdentity) => _inner.ProbeRead(filePath, expectedIdentity);

    public bool TryFlushVolume(string path) => _inner.TryFlushVolume(path);

    // ───── Modifications ─────
    public FileIdentity CreateDirectory(string path, byte[]? securityDescriptor = null)
        => Mutate(nameof(CreateDirectory), path, () => _inner.CreateDirectory(path, securityDescriptor));

    public FileIdentity CopyFile(string sourcePath, FileIdentity expectedSourceIdentity, string destinationPath, CopyFileOptions options, Action<long>? bytesCopied, CancellationToken cancellationToken)
        => Mutate(nameof(CopyFile), destinationPath, () => _inner.CopyFile(sourcePath, expectedSourceIdentity, destinationPath, options, bytesCopied, cancellationToken));

    public FileIdentity CreateHardLink(string newLinkPath, string existingFilePath, FileIdentity expectedExistingIdentity)
        => Mutate(nameof(CreateHardLink), newLinkPath, () => _inner.CreateHardLink(newLinkPath, existingFilePath, expectedExistingIdentity));

    public FileIdentity CreateReparsePoint(string path, bool isDirectory, ReparsePointData data)
        => Mutate(nameof(CreateReparsePoint), path, () => _inner.CreateReparsePoint(path, isDirectory, data));

    public void Rename(string path, FileIdentity expectedIdentity, string newName)
        => Mutate(nameof(Rename), path, () => _inner.Rename(path, expectedIdentity, newName));

    public void SetBasicInfo(string path, FileIdentity expectedIdentity, FileTimestamps times, FileAttributes attributes)
        => Mutate(nameof(SetBasicInfo), path, () => _inner.SetBasicInfo(path, expectedIdentity, times, attributes));

    public void SetSecurityDescriptor(string path, FileIdentity expectedIdentity, byte[] securityDescriptor, SecurityParts parts)
        => Mutate(nameof(SetSecurityDescriptor), path, () => _inner.SetSecurityDescriptor(path, expectedIdentity, securityDescriptor, parts));

    public bool TrySetShortName(string path, FileIdentity expectedIdentity, string shortName)
        => Mutate(nameof(TrySetShortName), path, () => _inner.TrySetShortName(path, expectedIdentity, shortName));

    public void Delete(string path, FileIdentity expectedIdentity)
        => Mutate(nameof(Delete), path, () => _inner.Delete(path, expectedIdentity));

    public void FlushFile(string path, FileIdentity expectedIdentity)
        => Mutate(nameof(FlushFile), path, () => _inner.FlushFile(path, expectedIdentity));

    public void AppendToFile(string path, ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Mutate(nameof(AppendToFile), path, () => _inner.AppendToFile(path, copy));
    }

    public void WriteNewFile(string path, ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Mutate(nameof(WriteNewFile), path, () => _inner.WriteNewFile(path, copy));
    }
}
