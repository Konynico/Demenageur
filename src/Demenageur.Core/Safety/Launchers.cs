using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;

namespace Demenageur.Core.Safety;

/// <summary>Ce que le lanceur sait faire pour changer un jeu de disque.</summary>
public enum LauncherMoveSupport
{
    /// <summary>Bouton « Déplacer » intégré (Steam, application Xbox) : à préférer à une jonction.</summary>
    NativeMove,

    /// <summary>Pas de bouton, mais le lanceur sait retrouver un jeu copié ailleurs (« Localiser », « Importer »).</summary>
    Relocate,

    /// <summary>Rien de documenté : réinstallation conseillée par l'éditeur.</summary>
    None,
}

/// <summary>Lanceur de jeux connu.</summary>
public sealed record Launcher(string Id, string Name, LauncherMoveSupport Support, string Advice);

/// <summary>Dossier géré par un lanceur : une bibliothèque entière (Steam) ou le dossier d'un jeu.</summary>
public sealed record ManagedLocation(string LauncherId, string Path, string? Title = null);

/// <summary>
/// Lanceurs connus, avec la marche à suivre que chacun propose. Vérifié en novembre 2026 :
/// seuls Steam et l'application Xbox ont un vrai bouton « Déplacer ».
/// </summary>
public static class KnownLaunchers
{
    public static readonly Launcher Steam = new("steam", "Steam", LauncherMoveSupport.NativeMove,
        "Steam sait déplacer ce jeu lui-même : Steam → Paramètres → Stockage, sélectionne le jeu puis « Déplacer » "
        + "(ou clic droit sur le jeu → Propriétés → Fichiers installés → Déplacer le dossier d'installation). "
        + "Avec une jonction, Steam prépare ses mises à jour sur le disque d'origine puis doit les recopier vers l'autre disque : elles peuvent échouer.");

    public static readonly Launcher Xbox = new("xbox", "Application Xbox", LauncherMoveSupport.NativeMove,
        "L'application Xbox sait déplacer ce jeu elle-même : clic droit sur le jeu → Gérer → Fichiers → Déplacer. "
        + "Ses dossiers sont protégés par Windows (Gaming Services) : une jonction risque de casser les mises à jour.");

    public static readonly Launcher Epic = new("epic", "Epic Games", LauncherMoveSupport.Relocate,
        "Epic Games n'a pas de bouton « Déplacer », mais documente une procédure : copier le jeu, lancer une installation vers le nouveau dossier, l'interrompre, "
        + "y remettre les fichiers puis reprendre (le lanceur vérifie au lieu de télécharger). La jonction fonctionne aussi : Epic continue d'utiliser le même chemin.");

    public static readonly Launcher Gog = new("gog", "GOG Galaxy", LauncherMoveSupport.Relocate,
        "GOG Galaxy n'a pas de bouton « Déplacer » : on peut copier le jeu puis utiliser « Gérer l'installation → Importer un dossier ». La jonction fonctionne aussi.");

    public static readonly Launcher Ubisoft = new("ubisoft", "Ubisoft Connect", LauncherMoveSupport.Relocate,
        "Ubisoft Connect n'a pas de bouton « Déplacer » : on peut copier le jeu puis utiliser « Localiser le jeu installé ». La jonction fonctionne aussi.");

    public static readonly Launcher BattleNet = new("battlenet", "Battle.net", LauncherMoveSupport.Relocate,
        "Battle.net n'a pas de bouton « Déplacer » : on peut copier le jeu puis utiliser « Localiser le jeu ». Ne déplace rien pendant une mise à jour du jeu.");

    public static readonly Launcher Ea = new("ea", "EA app", LauncherMoveSupport.None,
        "L'EA app ne sait pas déplacer un jeu : EA conseille de le désinstaller puis de le réinstaller sur l'autre disque. La jonction évite le retéléchargement, sans garantie d'EA.");

    public static readonly Launcher Riot = new("riot", "Riot Client", LauncherMoveSupport.None,
        "Riot conseille de désinstaller puis réinstaller. L'anti-triche Vanguard surveille les fichiers du jeu : une jonction peut provoquer des refus de lancement.");

    public static readonly Launcher Rockstar = new("rockstar", "Rockstar Games Launcher", LauncherMoveSupport.None,
        "Le lanceur Rockstar ne documente pas de déplacement. La jonction devrait fonctionner, sans garantie de Rockstar ; en cas de souci, annule le déplacement.");

    public static IReadOnlyList<Launcher> All { get; } = [Steam, Xbox, Epic, Gog, Ubisoft, BattleNet, Ea, Riot, Rockstar];

    public static Launcher? Get(string id) => All.FirstOrDefault(l => l.Id == id);
}

/// <summary>Analyse (pure, sans accès disque) des fichiers et entrées laissés par les lanceurs.</summary>
public static partial class LauncherDetection
{
    /// <summary>Chemins des bibliothèques dans <c>steamapps\libraryfolders.vdf</c> (format KeyValues de Valve).</summary>
    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdf)
        => SteamPathRegex().Matches(vdf)
            .Select(m => m.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal))
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Dossier et nom d'un jeu dans un manifeste Epic (<c>…\Data\Manifests\*.item</c>, JSON).</summary>
    public static (string Path, string? Title)? ParseEpicManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String
                && NormalizePath(location.GetString()) is { } path)
            {
                var title = root.TryGetProperty("DisplayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                return (path, title);
            }
        }
        catch (JsonException)
        {
            // Manifeste illisible : ignoré.
        }

        return null;
    }

    /// <summary>
    /// Dossiers racines des jeux Xbox d'un disque, d'après <c>X:\.GamingRoot</c> :
    /// « RGBX », un compteur sur 4 octets, puis des chemins relatifs en UTF-16 terminés par un zéro.
    /// </summary>
    public static IReadOnlyList<string> ParseGamingRoot(ReadOnlySpan<byte> content, string driveRoot)
    {
        if (content.Length < 8 || content[0] != (byte)'R' || content[1] != (byte)'G' || content[2] != (byte)'B' || content[3] != (byte)'X')
        {
            return [];
        }

        var count = BitConverter.ToInt32(content[4..8]);
        var text = Encoding.Unicode.GetString(content[8..]);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Take(Math.Clamp(count, 0, 16))
            .Where(relative => !relative.Contains("..", StringComparison.Ordinal) && !relative.Contains(':', StringComparison.Ordinal))
            .Select(relative => PathUtil.Join(PathUtil.TrimTrailingSeparator(driveRoot), relative.Trim('\\')))
            .ToList();
    }

    /// <summary>Lanceur déduit d'une entrée de désinstallation (commande de désinstallation, éditeur), ou null.</summary>
    public static Launcher? FromUninstallEntry(UninstallEntry entry)
    {
        var uninstall = entry.UninstallString ?? string.Empty;
        var publisher = entry.Publisher ?? string.Empty;
        bool Has(string text) => uninstall.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("steam://uninstall/"))
        {
            return KnownLaunchers.Steam;
        }

        if (Has("uplay://uninstall/"))
        {
            return KnownLaunchers.Ubisoft;
        }

        if (Has("com.epicgames.launcher://"))
        {
            return KnownLaunchers.Epic;
        }

        // Battle.net : les jeux ont le désinstalleur de l'agent, avec un --uid autre que celui du lanceur lui-même.
        if (Has("Blizzard Uninstaller") && !Has("--uid=battle.net"))
        {
            return KnownLaunchers.BattleNet;
        }

        if (Has("RiotClientServices.exe") && Has("--uninstall-product=") && !Has("--uninstall-product=Riot_Client"))
        {
            return KnownLaunchers.Riot;
        }

        if (publisher.StartsWith("Rockstar Games", StringComparison.OrdinalIgnoreCase) && Has("-uninstall=") && !Has("-uninstall=launcher"))
        {
            return KnownLaunchers.Rockstar;
        }

        if (publisher.Equals("GOG.com", StringComparison.OrdinalIgnoreCase) && !entry.DisplayName.StartsWith("GOG GALAXY", StringComparison.OrdinalIgnoreCase))
        {
            return KnownLaunchers.Gog;
        }

        if (publisher.StartsWith("Electronic Arts", StringComparison.OrdinalIgnoreCase) && Has("EAInstaller"))
        {
            return KnownLaunchers.Ea;
        }

        return null;
    }

    /// <summary>Chemin local nettoyé (barres obliques normalisées, sans barre finale), ou null s'il n'est pas exploitable.</summary>
    public static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = PathUtil.TrimTrailingSeparator(value.Trim().Trim('"').Replace('/', '\\'));
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' ? path : null;
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();
}
