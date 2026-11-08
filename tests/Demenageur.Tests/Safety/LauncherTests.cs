using System.Text;
using Demenageur.Core.Inventory;
using Demenageur.Core.Safety;
using Demenageur.Tests.Infrastructure;
using Demenageur.Tests.Inventory;

namespace Demenageur.Tests.Safety;

/// <summary>Détection des jeux gérés par un lanceur (Steam, Epic, Xbox…) et exclusions manuelles.</summary>
public sealed class LauncherTests
{
    // ── Analyse des fichiers des lanceurs ──

    [Fact]
    public void Steam_library_folders_are_read_from_libraryfolders_vdf()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"295236345"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            	"2"
            	{
            		"path"		"E:\\SteamLibrary\\"
            	}
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\SteamLibrary"], LauncherDetection.ParseSteamLibraryFolders(vdf));
    }

    [Fact]
    public void Epic_manifest_gives_the_game_folder_and_title()
    {
        var game = LauncherDetection.ParseEpicManifest("""{ "FormatVersion": 0, "DisplayName": "Fortnite", "InstallLocation": "D:/Jeux/EpicGame/Fortnite/" }""");
        Assert.Equal((@"D:\Jeux\EpicGame\Fortnite", "Fortnite"), game);
        Assert.Null(LauncherDetection.ParseEpicManifest("pas du JSON"));
        Assert.Null(LauncherDetection.ParseEpicManifest("""{ "InstallLocation": "relatif\\dossier" }"""));
    }

    [Fact]
    public void Xbox_gaming_root_file_is_decoded()
    {
        // Octets réels d'un fichier E:\.GamingRoot : « RGBX », compteur 1, « jeux » en UTF-16.
        byte[] content = [0x52, 0x47, 0x42, 0x58, 0x01, 0x00, 0x00, 0x00, .. Encoding.Unicode.GetBytes("jeux\0")];
        Assert.Equal([@"E:\jeux"], LauncherDetection.ParseGamingRoot(content, @"E:\"));
        Assert.Empty(LauncherDetection.ParseGamingRoot("XXXX"u8, @"E:\"));

        byte[] hostile = [0x52, 0x47, 0x42, 0x58, 0x01, 0x00, 0x00, 0x00, .. Encoding.Unicode.GetBytes(@"..\Windows" + "\0")];
        Assert.Empty(LauncherDetection.ParseGamingRoot(hostile, @"C:\"));
    }

    [Theory]
    [InlineData("Teardown", "Tuxedo Labs", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/1167630", "steam")]
    [InlineData("Tom Clancy's Rainbow Six Siege", "Ubisoft Montreal", "\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe\" uplay://uninstall/635", "ubisoft")]
    [InlineData("Overwatch", "Blizzard Entertainment", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=frFR --uid=prometheus --displayname=\"Overwatch\"", "battlenet")]
    [InlineData("Battle.net", "Blizzard Entertainment", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=frFR --uid=battle.net --displayname=\"Battle.net\"", null)]
    [InlineData("VALORANT", "Riot Games, Inc", "\"D:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live", "riot")]
    [InlineData("Riot Client", "Riot Games, Inc", "\"D:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=Riot_Client --uninstall-patchline=", null)]
    [InlineData("Grand Theft Auto V Enhanced", "Rockstar Games", "\"D:\\Jeux\\RockStarGames\\Grand Theft Auto V Enhanced\\uninstall.exe\" -enableFullMode -uninstall=gta5_gen9", "rockstar")]
    [InlineData("Rockstar Games Launcher", "Rockstar Games", "\"E:\\jeux\\Rockstar Games\\Launcher\\uninstall.exe\" -enableFullMode -uninstall=launcher", null)]
    [InlineData("Cyberpunk 2077", "GOG.com", "\"E:\\jeux\\GOG\\Cyberpunk 2077\\unins000.exe\"", "gog")]
    [InlineData("GOG GALAXY", "GOG.com", "\"C:\\Program Files (x86)\\GOG Galaxy\\unins000.exe\"", null)]
    [InlineData("7-Zip", "Igor Pavlov", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\"", null)]
    public void Launcher_is_recognised_from_the_uninstall_entry(string name, string publisher, string uninstall, string? expected)
    {
        var entry = new UninstallEntry { RegistryKey = "k", Scope = RegistryScope.Machine64, DisplayName = name, Publisher = publisher, UninstallString = uninstall };
        Assert.Equal(expected, LauncherDetection.FromUninstallEntry(entry)?.Id);
    }

    // ── Règles ──

    private static MoveCandidate Candidate(string folder, string name = "Jeu", params UninstallEntry[] entries)
        => new(folder, name, null, entries.Length > 0 ? entries : [new UninstallEntry { RegistryKey = "HKLM\\X\\" + name, Scope = RegistryScope.Machine64, DisplayName = name }], FolderSource.InstallLocation);

    private static IReadOnlyList<RuleVerdict> Evaluate(TestSandbox sandbox, SandboxKnownPaths known, MoveCandidate candidate, SystemSnapshot system)
        => new SafetyRules(known, sandbox.RawFs).Evaluate(candidate, system, []);

    private static string Folder(TestSandbox sandbox, params string[] parts)
    {
        var path = sandbox.PathOf(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void A_steam_game_asks_for_confirmation_and_recommends_steam()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var library = Folder(sandbox, "SteamLibrary", "steamapps");
        var game = Folder(sandbox, "SteamLibrary", "steamapps", "common", "Rust");
        var system = new SystemSnapshot { LauncherLocations = [new ManagedLocation("steam", library)] };

        var verdict = Assert.Single(Evaluate(sandbox, known, Candidate(game, "Rust"), system), v => v.RuleId == SafetyRules.LauncherRuleId);

        Assert.Equal(RuleLevel.Confirm, verdict.Level);
        Assert.Equal("Géré par Steam", verdict.Title);
        Assert.Contains("Stockage", verdict.Explanation);
    }

    [Fact]
    public void An_epic_game_is_only_a_warning()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var game = Folder(sandbox, "EpicGame", "Fortnite");
        var system = new SystemSnapshot { LauncherLocations = [new ManagedLocation("epic", game, "Fortnite")] };

        var verdict = Assert.Single(Evaluate(sandbox, known, Candidate(game, "Fortnite"), system), v => v.RuleId == SafetyRules.LauncherRuleId);

        Assert.Equal(RuleLevel.Warning, verdict.Level);
        Assert.Contains("« Fortnite »", verdict.Explanation);
    }

    [Fact]
    public void The_most_specific_location_wins_a_gog_game_inside_the_xbox_root()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var xboxRoot = Folder(sandbox, "jeux");
        var cyberpunk = Folder(sandbox, "jeux", "GOG", "Cyberpunk 2077");
        var system = new SystemSnapshot
        {
            LauncherLocations = [new ManagedLocation("xbox", xboxRoot), new ManagedLocation("gog", cyberpunk, "Cyberpunk 2077")],
        };

        var verdict = Assert.Single(Evaluate(sandbox, known, Candidate(cyberpunk), system), v => v.RuleId == SafetyRules.LauncherRuleId);
        Assert.Equal("Géré par GOG Galaxy", verdict.Title);
    }

    [Fact]
    public void Launchers_without_scannable_library_are_recognised_from_the_uninstall_entry()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var overwatch = Folder(sandbox, "BattleNet", "Overwatch");
        var entry = new UninstallEntry
        {
            RegistryKey = "k", Scope = RegistryScope.Machine32, DisplayName = "Overwatch", Publisher = "Blizzard Entertainment",
            UninstallString = "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --uid=prometheus",
        };

        var verdict = Assert.Single(Evaluate(sandbox, known, Candidate(overwatch, "Overwatch", entry), new SystemSnapshot()), v => v.RuleId == SafetyRules.LauncherRuleId);
        Assert.Equal("Géré par Battle.net", verdict.Title);
        Assert.Equal(RuleLevel.Warning, verdict.Level);
    }

    [Fact]
    public void A_folder_containing_a_library_is_flagged()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var steamClient = Folder(sandbox, "Steam");
        var library = Folder(sandbox, "Steam", "steamapps");
        var system = new SystemSnapshot { LauncherLocations = [new ManagedLocation("steam", library)] };

        Assert.Contains(Evaluate(sandbox, known, Candidate(steamClient, "Steam"), system),
            v => v.RuleId == SafetyRules.LauncherRuleId && v.Title == "Contient des jeux Steam" && v.Level == RuleLevel.Confirm);
    }

    [Fact]
    public void An_ordinary_program_is_not_attributed_to_a_launcher()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var app = Folder(sandbox, "Programmes", "7-Zip");
        var system = new SystemSnapshot { LauncherLocations = [new ManagedLocation("steam", Folder(sandbox, "SteamLibrary", "steamapps"))] };

        Assert.DoesNotContain(Evaluate(sandbox, known, Candidate(app, "7-Zip"), system), v => v.RuleId == SafetyRules.LauncherRuleId);
    }

    [Fact]
    public void A_user_exclusion_blocks_the_folder_its_subfolders_and_its_parents()
    {
        using var sandbox = new TestSandbox();
        var known = new SandboxKnownPaths(sandbox);
        var parent = Folder(sandbox, "Jeux");
        var excluded = Folder(sandbox, "Jeux", "Sauvegardes");
        var inside = Folder(sandbox, "Jeux", "Sauvegardes", "Partie1");
        var sibling = Folder(sandbox, "Jeux", "Autre");
        var system = new SystemSnapshot { UserExclusions = [new UserExclusion(excluded, "à garder ici")] };

        foreach (var folder in new[] { excluded, inside, parent })
        {
            var verdict = Assert.Single(Evaluate(sandbox, known, Candidate(folder), system), v => v.RuleId == "R-EXCLU");
            Assert.Equal(RuleLevel.Block, verdict.Level);
            Assert.Contains("à garder ici", verdict.Explanation);
        }

        Assert.DoesNotContain(Evaluate(sandbox, known, Candidate(sibling), system), v => v.RuleId == "R-EXCLU");
    }
}
