using System.Text.RegularExpressions;

namespace Demenageur.Core.Inventory;

/// <summary>
/// Développe les variables d'environnement d'une valeur REG_EXPAND_SZ selon le CONTEXTE de l'entrée :
/// <list type="bullet">
/// <item>vue 32 bits : <c>%ProgramFiles%</c> désigne <c>Program Files (x86)</c> ;</item>
/// <item>entrée d'un profil (HKEY_USERS) : <c>%USERPROFILE%</c>, <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c> désignent
/// le profil de CET utilisateur, pas celui du compte administrateur qui exécute l'app.</item>
/// </list>
/// </summary>
public static partial class RegistryValueExpander
{
    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariablePattern();

    public static string Expand(string value, bool is32BitView, string? userProfile, Func<string, string?> environment)
    {
        return VariablePattern().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            var replacement = name.ToUpperInvariant() switch
            {
                "PROGRAMFILES" when is32BitView => environment("ProgramFiles(x86)"),
                "COMMONPROGRAMFILES" when is32BitView => environment("CommonProgramFiles(x86)"),
                "USERPROFILE" when userProfile is not null => userProfile,
                "LOCALAPPDATA" when userProfile is not null => userProfile + @"\AppData\Local",
                "APPDATA" when userProfile is not null => userProfile + @"\AppData\Roaming",
                "HOMEPATH" when userProfile is not null && userProfile.Length > 2 => userProfile[2..],
                _ => environment(name),
            };
            return replacement ?? match.Value;
        });
    }
}
