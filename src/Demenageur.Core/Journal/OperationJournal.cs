using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Journal;

/// <summary>Étapes inscrites au journal d'une opération.</summary>
public static class JournalSteps
{
    public const string Planned = "Planned";
    public const string DestinationIntent = "DestinationIntent";
    public const string DestinationCreated = "DestinationCreated";
    public const string LockIntent = "LockIntent";
    public const string Locked = "Locked";
    public const string ManifestSaved = "ManifestSaved";
    public const string Copied = "Copied";
    public const string ContentVerified = "ContentVerified";
    public const string SecurityVerified = "SecurityVerified";
    public const string JunctionIntent = "JunctionIntent";
    public const string JunctionCreated = "JunctionCreated";
    public const string JunctionRemoved = "JunctionRemoved";
    public const string RestoreIntent = "RestoreIntent";
    public const string Restored = "Restored";
    public const string Committed = "Committed";
    public const string CleanupDone = "CleanupDone";
    public const string CleanupPending = "CleanupPending";
    public const string Completed = "Completed";
    public const string RollbackStarted = "RollbackStarted";
    public const string RolledBack = "RolledBack";
    public const string NeedsAttention = "NeedsAttention";
    public const string Warning = "Warning";
    public const string Info = "Info";

    /// <summary>Après ces étapes, l'opération est terminée (rien à reprendre).</summary>
    public static bool IsTerminal(string step) => step is Completed or RolledBack;
}

/// <summary>Enregistrement du journal.</summary>
public sealed record JournalRecord(int Sequence, DateTimeOffset Time, string Step, string? Message, IReadOnlyDictionary<string, string> Data)
{
    public string? Get(string key) => Data.TryGetValue(key, out var value) ? value : null;
}

/// <summary>Contenu relu d'un journal.</summary>
public sealed record JournalContent(IReadOnlyList<JournalRecord> Records, bool TornTail, bool Corrupt)
{
    public JournalRecord? Last => Records.Count > 0 ? Records[^1] : null;

    public bool Has(string step) => Records.Any(r => r.Step == step);

    public JournalRecord? LastOf(string step) => Records.LastOrDefault(r => r.Step == step);
}

/// <summary>
/// Journal d'opération en ajout seul : une ligne par étape, « CRC32 JSON ». Chaque ajout est écrit directement
/// sur le disque. Une ligne finale tronquée (coupure de courant pendant l'écriture) est ignorée ;
/// une ligne invalide au milieu rend le journal « corrompu » : aucune action automatique n'est alors entreprise.
/// </summary>
public sealed class OperationJournal
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IFileSystem _fs;
    private readonly Func<DateTimeOffset> _clock;
    private int _sequence;

    public OperationJournal(IFileSystem fs, string path, Func<DateTimeOffset>? clock = null, int lastSequence = 0)
    {
        _fs = fs;
        Path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _sequence = lastSequence;
    }

    public string Path { get; }

    public JournalRecord Append(string step, string? message = null, IReadOnlyDictionary<string, string>? data = null)
    {
        var record = new JournalRecord(++_sequence, _clock(), step, message, data ?? new Dictionary<string, string>());
        var json = Serialize(record);
        var crc = Crc32.HashToUInt32(json);
        var line = Encoding.UTF8.GetBytes(crc.ToString("x8", CultureInfo.InvariantCulture) + " ");
        var buffer = new byte[line.Length + json.Length + 1];
        line.CopyTo(buffer, 0);
        json.CopyTo(buffer, line.Length);
        buffer[^1] = (byte)'\n';
        _fs.AppendToFile(Path, buffer);
        return record;
    }

    public static JournalContent Read(IFileSystem fs, string path)
    {
        var bytes = fs.ReadAllBytes(path);
        return bytes is null ? new JournalContent([], false, false) : Parse(bytes);
    }

    public static JournalContent Parse(byte[] bytes)
    {
        var records = new List<JournalRecord>();
        var start = 0;
        var tornTail = false;
        var corrupt = false;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var isLastLine = end < 0;
            var lineEnd = isLastLine ? bytes.Length : end;
            var line = bytes.AsSpan(start, lineEnd - start);
            start = lineEnd + 1;
            if (line.IsEmpty)
            {
                continue;
            }

            if (TryParseLine(line, out var record) && !isLastLine)
            {
                records.Add(record!);
                continue;
            }

            if (isLastLine || start >= bytes.Length)
            {
                // Dernière ligne invalide ou sans fin de ligne : écriture interrompue, on l'ignore.
                tornTail = true;
                break;
            }

            corrupt = true;
            break;
        }

        return new JournalContent(records, tornTail, corrupt);
    }

    private static bool TryParseLine(ReadOnlySpan<byte> line, out JournalRecord? record)
    {
        record = null;
        if (line.Length < 10 || line[8] != (byte)' ')
        {
            return false;
        }

        if (!uint.TryParse(Encoding.ASCII.GetString(line[..8]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc))
        {
            return false;
        }

        var json = line[9..];
        if (Crc32.HashToUInt32(json) != crc)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("data", out var dataElement))
            {
                foreach (var property in dataElement.EnumerateObject())
                {
                    data[property.Name] = LosslessText.Decode(property.Value.GetString() ?? string.Empty);
                }
            }

            record = new JournalRecord(
                root.GetProperty("seq").GetInt32(),
                root.GetProperty("time").GetDateTimeOffset(),
                root.GetProperty("step").GetString()!,
                root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? LosslessText.Decode(message.GetString()!) : null,
                data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Serialize(JournalRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", record.Sequence);
            writer.WriteString("time", record.Time);
            writer.WriteString("step", record.Step);
            if (record.Message is not null)
            {
                writer.WriteString("message", LosslessText.Encode(record.Message));
            }

            writer.WriteStartObject("data");
            foreach (var (key, value) in record.Data)
            {
                writer.WriteString(key, LosslessText.Encode(value));
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
