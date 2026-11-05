using System.Globalization;
using System.Text;

namespace Demenageur.Core.Journal;

/// <summary>
/// Encodage réversible d'un texte UTF-16 quelconque (un nom NTFS peut contenir un surrogate isolé,
/// qu'aucun encodage UTF-8/JSON ne sait représenter) : « % » devient « %25 », un surrogate isolé « %uXXXX ».
/// </summary>
public static class LosslessText
{
    public static string Encode(string text)
    {
        if (!NeedsEncoding(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%')
            {
                builder.Append("%25");
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                builder.Append("%u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static string Decode(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '%')
            {
                builder.Append(c);
            }
            else if (string.CompareOrdinal(text, i, "%25", 0, 3) == 0)
            {
                builder.Append('%');
                i += 2;
            }
            else if (i + 5 < text.Length && text[i + 1] == 'u')
            {
                builder.Append((char)int.Parse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else
            {
                throw new FormatException($"Séquence d'échappement invalide à la position {i}.");
            }
        }

        return builder.ToString();
    }

    private static bool NeedsEncoding(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%')
            {
                return true;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
