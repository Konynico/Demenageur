using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Journal;

/// <summary>Sérialisation JSON du manifeste (noms encodés sans perte, descripteurs de sécurité dédupliqués).</summary>
public static class ManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize(Manifest manifest)
    {
        var descriptors = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int? DescriptorIndex(byte[]? sd)
        {
            if (sd is null)
            {
                return null;
            }

            var key = Convert.ToBase64String(sd);
            if (!index.TryGetValue(key, out var i))
            {
                i = descriptors.Count;
                descriptors.Add(key);
                index[key] = i;
            }

            return i;
        }

        var dto = new ManifestDto
        {
            Version = 1,
            SourceRoot = LosslessText.Encode(manifest.SourceRoot),
            Entries = manifest.Entries.Select(e => new EntryDto
            {
                Path = LosslessText.Encode(e.RelativePath),
                Kind = e.Kind.ToString(),
                Attributes = (int)e.Attributes,
                Size = e.Size,
                Created = e.Times.CreationTime,
                Accessed = e.Times.LastAccessTime,
                Written = e.Times.LastWriteTime,
                Identity = e.SourceIdentity.ToString(),
                LinkCount = e.LinkCount,
                ShortName = e.ShortName is null ? null : LosslessText.Encode(e.ShortName),
                Reparse = e.Reparse is null ? null : Convert.ToBase64String(e.Reparse),
                Security = DescriptorIndex(e.SecurityDescriptor),
                Streams = e.Streams.Count == 0 ? null : e.Streams.Select(s => new StreamDto { Name = LosslessText.Encode(s.Name), Size = s.Size, Hash = s.Hash?.ToString() }).ToList(),
                HardLinkPrimary = e.HardLinkPrimary is null ? null : LosslessText.Encode(e.HardLinkPrimary),
                Hash = e.Hash?.ToString(),
                DestinationIdentity = e.DestinationIdentity?.ToString(),
            }).ToList(),
            SecurityDescriptors = descriptors,
        };
        return JsonSerializer.SerializeToUtf8Bytes(dto, Options);
    }

    public static Manifest Deserialize(byte[] json)
    {
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, Options) ?? throw new FormatException("Manifeste vide.");
        if (dto.Version != 1)
        {
            throw new FormatException($"Version de manifeste inconnue : {dto.Version}.");
        }

        var descriptors = dto.SecurityDescriptors.Select(Convert.FromBase64String).ToList();
        return new Manifest
        {
            SourceRoot = LosslessText.Decode(dto.SourceRoot),
            Entries = dto.Entries.Select(e => new ManifestEntry
            {
                RelativePath = LosslessText.Decode(e.Path),
                Kind = Enum.Parse<FsEntryKind>(e.Kind),
                Attributes = (FileAttributes)e.Attributes,
                Size = e.Size,
                Times = new FileTimestamps(e.Created, e.Accessed, e.Written),
                SourceIdentity = FileIdentity.Parse(e.Identity),
                LinkCount = e.LinkCount,
                ShortName = e.ShortName is null ? null : LosslessText.Decode(e.ShortName),
                Reparse = e.Reparse is null ? null : Convert.FromBase64String(e.Reparse),
                SecurityDescriptor = e.Security is { } i ? descriptors[i] : null,
                Streams = (e.Streams ?? []).Select(s => new ManifestStream { Name = LosslessText.Decode(s.Name), Size = s.Size, Hash = s.Hash is null ? null : ContentHash.Parse(s.Hash) }).ToList(),
                HardLinkPrimary = e.HardLinkPrimary is null ? null : LosslessText.Decode(e.HardLinkPrimary),
                Hash = e.Hash is null ? null : ContentHash.Parse(e.Hash),
                DestinationIdentity = e.DestinationIdentity is null ? null : FileIdentity.Parse(e.DestinationIdentity),
            }).ToList(),
        };
    }

    private sealed class ManifestDto
    {
        [JsonPropertyName("version")] public int Version { get; set; }

        [JsonPropertyName("sourceRoot")] public string SourceRoot { get; set; } = string.Empty;

        [JsonPropertyName("entries")] public List<EntryDto> Entries { get; set; } = [];

        [JsonPropertyName("securityDescriptors")] public List<string> SecurityDescriptors { get; set; } = [];
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("p")] public string Path { get; set; } = string.Empty;

        [JsonPropertyName("k")] public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("a")] public int Attributes { get; set; }

        [JsonPropertyName("s")] public long Size { get; set; }

        [JsonPropertyName("ct")] public long Created { get; set; }

        [JsonPropertyName("at")] public long Accessed { get; set; }

        [JsonPropertyName("wt")] public long Written { get; set; }

        [JsonPropertyName("id")] public string Identity { get; set; } = string.Empty;

        [JsonPropertyName("lc")] public uint LinkCount { get; set; } = 1;

        [JsonPropertyName("sn")] public string? ShortName { get; set; }

        [JsonPropertyName("rp")] public string? Reparse { get; set; }

        [JsonPropertyName("sd")] public int? Security { get; set; }

        [JsonPropertyName("st")] public List<StreamDto>? Streams { get; set; }

        [JsonPropertyName("hl")] public string? HardLinkPrimary { get; set; }

        [JsonPropertyName("h")] public string? Hash { get; set; }

        [JsonPropertyName("did")] public string? DestinationIdentity { get; set; }
    }

    private sealed class StreamDto
    {
        [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;

        [JsonPropertyName("s")] public long Size { get; set; }

        [JsonPropertyName("h")] public string? Hash { get; set; }
    }
}
