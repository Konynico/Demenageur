using System.Security.Principal;
using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>Clés des données inscrites au journal.</summary>
public static class JournalKeys
{
    public const string Kind = "kind";
    public const string Software = "software";
    public const string Source = "source";
    public const string Destination = "destination";
    public const string Locked = "locked";
    public const string Restore = "restore";
    public const string MoveOperation = "moveOperation";
    public const string SourceId = "sourceId";
    public const string DestinationId = "destinationId";
    public const string LockedId = "lockedId";
    public const string RestoreId = "restoreId";
    public const string JunctionId = "junctionId";
    public const string Files = "files";
    public const string Bytes = "bytes";
    public const string Failures = "failures";
    public const string RemoveJunction = "removeJunction";
}

/// <summary>Opération enregistrée (journal relu).</summary>
public sealed class StoredOperation
{
    public StoredOperation(string id, string directory, JournalContent journal, bool trusted)
    {
        Id = id;
        Directory = directory;
        Journal = journal;
        Trusted = trusted;
        Header = journal.Records.FirstOrDefault(r => r.Step == JournalSteps.Planned);
    }

    public string Id { get; }

    public string Directory { get; }

    public JournalContent Journal { get; }

    /// <summary>Faux si le journal n'appartient pas à un administrateur (ou à SYSTEM) : il est alors ignoré.</summary>
    public bool Trusted { get; }

    public JournalRecord? Header { get; }

    public OperationKind Kind => Header?.Get(JournalKeys.Kind) switch
    {
        nameof(OperationKind.Undo) => OperationKind.Undo,
        nameof(OperationKind.Retire) => OperationKind.Retire,
        _ => OperationKind.Move,
    };

    public string SoftwareName => Header?.Get(JournalKeys.Software) ?? "(inconnu)";

    public string? SourcePath => Header?.Get(JournalKeys.Source);

    public string? DestinationPath => Header?.Get(JournalKeys.Destination);

    public string? LockedPath => Header?.Get(JournalKeys.Locked);

    public string? RestorePath => Header?.Get(JournalKeys.Restore);

    public string? LastStep => Journal.Last?.Step;

    public bool IsTerminal => LastStep is { } step && JournalSteps.IsTerminal(step);

    /// <summary>Journal sans aucun enregistrement valide (interruption avant même la première étape) : rien n'a été fait.</summary>
    public bool IsEmpty => Trusted && Journal.Records.Count == 0 && !Journal.Corrupt;

    public bool IsCommitted => Journal.Has(JournalSteps.Committed);

    public bool IsCompleted => LastStep == JournalSteps.Completed;

    public bool IsReadable => Trusted && !Journal.Corrupt && Header is not null && SourcePath is not null && DestinationPath is not null && LockedPath is not null;

    public DateTimeOffset? Started => Header?.Time;

    public FileIdentity? IdentityFrom(string step, string key)
        => Journal.LastOf(step)?.Get(key) is { } text ? FileIdentity.Parse(text) : null;
}

/// <summary>
/// Dossier des opérations : un sous-dossier par opération (journal + manifestes).
/// Les journaux dont le propriétaire n'est pas de confiance sont ignorés.
/// </summary>
public sealed class OperationStore
{
    public const string JournalFileName = "journal.jsonl";
    public const string ManifestFileName = "manifest.json";
    public const string VerifiedManifestFileName = "manifest.verifie.json";

    private readonly IFileSystem _fs;
    private readonly IReadOnlySet<SecurityIdentifier> _trustedOwners;

    public OperationStore(IFileSystem fs, string root, IEnumerable<SecurityIdentifier>? trustedOwners = null)
    {
        _fs = fs;
        Root = PathUtil.TrimTrailingSeparator(root);
        _trustedOwners = (trustedOwners ?? DefaultTrustedOwners()).ToHashSet();
    }

    public string Root { get; }

    public static IEnumerable<SecurityIdentifier> DefaultTrustedOwners() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    public string DirectoryOf(string operationId)
    {
        if (!OperationIds.IsValid(operationId))
        {
            throw new ArgumentException($"Identifiant d'opération invalide : « {operationId} ».", nameof(operationId));
        }

        return PathUtil.Join(Root, operationId);
    }

    public string JournalPathOf(string operationId) => PathUtil.Join(DirectoryOf(operationId), JournalFileName);

    public IReadOnlyList<StoredOperation> List()
    {
        var root = _fs.GetEntry(Root);
        if (root is null || root.Kind != FsEntryKind.Directory)
        {
            return [];
        }

        var result = new List<StoredOperation>();
        foreach (var child in _fs.ListDirectory(Root, root.Identity))
        {
            if ((child.Attributes & FileAttributes.Directory) != 0 && (child.Attributes & FileAttributes.ReparsePoint) == 0 && OperationIds.IsValid(child.Name))
            {
                if (Load(child.Name) is { } operation)
                {
                    result.Add(operation);
                }
            }
        }

        return result.OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    public StoredOperation? Load(string operationId)
    {
        var journalPath = JournalPathOf(operationId);
        var entry = _fs.GetEntry(journalPath);
        if (entry is null || entry.Kind != FsEntryKind.File)
        {
            return null;
        }

        var owner = SecurityDescriptorTools.GetOwner(_fs.GetSecurityDescriptor(journalPath, entry.Identity));
        var trusted = owner is not null && _trustedOwners.Contains(owner);
        var content = trusted ? OperationJournal.Read(_fs, journalPath) : new JournalContent([], false, false);
        return new StoredOperation(operationId, DirectoryOf(operationId), content, trusted);
    }

    public Manifest? LoadManifest(string operationId, string fileName)
    {
        var bytes = _fs.ReadAllBytes(PathUtil.Join(DirectoryOf(operationId), fileName));
        return bytes is null ? null : ManifestSerializer.Deserialize(bytes);
    }
}
