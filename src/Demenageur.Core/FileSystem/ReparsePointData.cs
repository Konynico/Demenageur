using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Demenageur.Core.FileSystem;

/// <summary>Étiquettes de points d'analyse (reparse points) utiles à Déménageur.</summary>
public static class ReparseTags
{
    /// <summary>Jonction (ou point de montage de volume).</summary>
    public const uint MountPoint = 0xA0000003;

    /// <summary>Lien symbolique.</summary>
    public const uint Symlink = 0xA000000C;

    /// <summary>Fichier compressé par Windows Overlay Filter (compact.exe /exe).</summary>
    public const uint Wof = 0x80000017;

    /// <summary>Fichier dédupliqué.</summary>
    public const uint Dedup = 0x80000013;

    /// <summary>Alias d'exécution d'application du Store (WindowsApps).</summary>
    public const uint AppExecLink = 0x8000001B;

    /// <summary>Bit « nom de substitution » : l'élément redirige vers un autre nom (jonction, lien…).</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    /// <summary>Fichiers « placeholder » de stockage cloud (OneDrive…), toutes variantes.</summary>
    public static bool IsCloud(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    /// <summary>Points d'analyse dont la lecture normale renvoie les vraies données.</summary>
    public static bool IsDataTransparent(uint tag) => tag is Wof or Dedup;

    public static string Describe(uint tag) => tag switch
    {
        MountPoint => "jonction",
        Symlink => "lien symbolique",
        Wof => "fichier compressé (WOF)",
        Dedup => "fichier dédupliqué",
        AppExecLink => "alias d'application du Store",
        _ when IsCloud(tag) => "fichier cloud (OneDrive…)",
        _ => $"point d'analyse inconnu (0x{tag:X8})",
    };
}

/// <summary>
/// Contenu d'un point d'analyse de type jonction ou lien symbolique.
/// Les octets bruts sont conservés pour pouvoir recréer le lien à l'identique.
/// </summary>
public sealed class ReparsePointData
{
    private const int HeaderSize = 8;
    private const uint SymlinkFlagRelative = 1;

    private ReparsePointData(uint tag, byte[] raw, string? substituteName, string? printName, bool isRelative)
    {
        Tag = tag;
        Raw = raw;
        SubstituteName = substituteName;
        PrintName = printName;
        IsRelative = isRelative;
    }

    public uint Tag { get; }

    /// <summary>Tampon REPARSE_DATA_BUFFER complet, tel que lu sur le disque.</summary>
    public byte[] Raw { get; }

    /// <summary>Nom de substitution (forme NT, par exemple <c>\??\D:\Cible</c>).</summary>
    public string? SubstituteName { get; }

    /// <summary>Nom affichable (par exemple <c>D:\Cible</c>).</summary>
    public string? PrintName { get; }

    /// <summary>Lien symbolique relatif.</summary>
    public bool IsRelative { get; }

    public bool IsJunction => Tag == ReparseTags.MountPoint && !IsVolumeMountPoint;

    /// <summary>Point de montage d'un volume entier (et non jonction vers un dossier).</summary>
    public bool IsVolumeMountPoint
        => Tag == ReparseTags.MountPoint
           && SubstituteName is not null
           && SubstituteName.StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cible sous forme de chemin Win32 complet (<c>D:\Cible</c>) pour une jonction ou un lien absolu ;
    /// la cible relative brute pour un lien relatif.
    /// </summary>
    public string? Target
    {
        get
        {
            if (IsRelative)
            {
                return SubstituteName;
            }

            var name = SubstituteName ?? PrintName;
            if (name is null)
            {
                return null;
            }

            if (name.StartsWith(@"\??\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + name[8..];
            }

            return name.StartsWith(@"\??\", StringComparison.Ordinal) ? name[4..] : name;
        }
    }

    public static ReparsePointData Parse(byte[] raw)
    {
        if (raw.Length < HeaderSize)
        {
            throw new FormatException("Tampon de point d'analyse trop court.");
        }

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4));
        if (HeaderSize + dataLength > raw.Length)
        {
            throw new FormatException("Longueur de point d'analyse incohérente.");
        }

        if (tag is not (ReparseTags.MountPoint or ReparseTags.Symlink))
        {
            return new ReparsePointData(tag, raw, null, null, false);
        }

        var data = raw.AsSpan(HeaderSize, dataLength);
        var substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var printOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var printLength = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var isRelative = false;
        int pathBufferStart;
        if (tag == ReparseTags.Symlink)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            isRelative = (flags & SymlinkFlagRelative) != 0;
            pathBufferStart = 12;
        }
        else
        {
            pathBufferStart = 8;
        }

        var pathBuffer = data[pathBufferStart..];
        var substitute = ReadName(pathBuffer, substituteOffset, substituteLength);
        var print = ReadName(pathBuffer, printOffset, printLength);
        return new ReparsePointData(tag, raw, substitute, print, isRelative);
    }

    /// <summary>
    /// Construit le tampon d'une jonction vers <paramref name="targetFullPath"/>, identique à celui de <c>mklink /J</c>.
    /// </summary>
    public static ReparsePointData CreateJunction(string targetFullPath)
    {
        if (targetFullPath.Length < 3 || targetFullPath[1] != ':' || targetFullPath[2] != '\\')
        {
            throw new ArgumentException("La cible d'une jonction doit être un chemin local complet (X:\\…).", nameof(targetFullPath));
        }

        var substitute = @"\??\" + targetFullPath;
        var print = targetFullPath;
        // Conversion sans perte (un nom NTFS peut contenir un surrogate isolé).
        var substituteBytes = MemoryMarshal.AsBytes(substitute.AsSpan()).ToArray();
        var printBytes = MemoryMarshal.AsBytes(print.AsSpan()).ToArray();

        // PathBuffer = substitut + NUL + affichable + NUL.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var dataLength = 8 + pathBufferLength;
        var raw = new byte[HeaderSize + dataLength];
        var span = raw.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, ReparseTags.MountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(span[16..]);
        printBytes.CopyTo(span[(16 + substituteBytes.Length + 2)..]);
        return Parse(raw);
    }

    private static string ReadName(ReadOnlySpan<byte> pathBuffer, int offset, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (offset + length > pathBuffer.Length)
        {
            throw new FormatException("Nom de point d'analyse hors du tampon.");
        }

        // Décodage sans perte : pas de remplacement des surrogates isolés.
        return new string(MemoryMarshal.Cast<byte, char>(pathBuffer.Slice(offset, length)));
    }
}
