using System.Globalization;

namespace Demenageur.Core.FileSystem;

/// <summary>
/// Identité unique d'un fichier ou dossier : numéro de série 64 bits du volume + identifiant de fichier 128 bits
/// (sur NTFS, la référence de fichier 64 bits étendue à 128 bits). Un renommage conserve l'identité ;
/// une substitution (par exemple remplacer un dossier par une jonction) la change.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId)
{
    /// <summary>Partie basse 64 bits de l'identifiant (celle que renvoie l'énumération d'un dossier sur NTFS).</summary>
    public ulong FileIdLow64 => (ulong)(FileId & ulong.MaxValue);

    public bool IsEmpty => VolumeSerial == 0 && FileId == UInt128.Zero;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{VolumeSerial:X16}-{FileId:X32}");

    public static FileIdentity Parse(string text)
    {
        var parts = text.Split('-');
        if (parts.Length != 2)
        {
            throw new FormatException($"Identité de fichier invalide : « {text} ».");
        }

        return new FileIdentity(
            ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            UInt128.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
