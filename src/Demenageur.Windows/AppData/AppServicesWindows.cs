using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Demenageur.Core.Moving;

namespace Demenageur.Windows.AppData;

/// <summary>Réglages de l'application.</summary>
public sealed class AppSettings
{
    /// <summary>L'avertissement du premier lancement a été lu et accepté.</summary>
    public bool RisksAccepted { get; set; }

    /// <summary>Simulation uniquement : aucune opération réelle n'est possible.</summary>
    public bool SimulationOnly { get; set; }

    /// <summary>Proposer un point de restauration (coché par défaut).</summary>
    public bool RestorePointByDefault { get; set; } = true;

    /// <summary>Afficher les entrées masquées (composants système).</summary>
    public bool ShowHiddenEntries { get; set; }

    /// <summary>Nom du dossier racine de destination sur chaque disque.</summary>
    public string DestinationFolderName { get; set; } = Core.Safety.DestinationContainer.DefaultFolderName;
}

/// <summary>Lecture/écriture des réglages (JSON dans le dossier de données protégé).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "reglages.json");
    }

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>Écriture d'images de l'interface (version aperçu uniquement, pour le développement).</summary>
public static class PreviewFiles
{
    public static void Save(string path, byte[] content)
    {
        if (!path.Contains("Temp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Les images d'aperçu ne s'écrivent que dans un dossier temporaire.");
        }

        File.WriteAllBytes(path, content);
    }
}

/// <summary>Actions du shell (ouvrir un dossier dans l'Explorateur). L'app ne lance JAMAIS un logiciel déplacé.</summary>
public static class ShellActions
{
    public static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
    }
}

/// <summary>
/// Fichier <c>LISEZMOI-Demenageur.txt</c> à la racine de destination : liste des logiciels déplacés ici et
/// commandes de restauration manuelle, utile si Déménageur n'est plus disponible.
/// </summary>
public static class ReadmeWriter
{
    public const string FileName = "LISEZMOI-Demenageur.txt";

    public static void Update(string destinationRoot, IEnumerable<MovedSoftware> moves)
    {
        if (!Directory.Exists(destinationRoot))
        {
            return;
        }

        var here = moves.Where(m => string.Equals(Path.GetDirectoryName(m.Destination), destinationRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
        var text = new StringBuilder();
        text.AppendLine("Ce dossier contient des logiciels déplacés par Déménageur.");
        text.AppendLine("Chacun reste accessible depuis son emplacement d'origine grâce à une jonction NTFS.");
        text.AppendLine("NE SUPPRIME PAS ces dossiers : les logiciels ne fonctionneraient plus.");
        text.AppendLine();
        text.AppendLine("Pour annuler un déplacement, utilise de préférence Déménageur (« Mes déplacements »).");
        text.AppendLine("Procédure manuelle (invite de commandes en administrateur), pour chaque logiciel :");
        text.AppendLine("  1. rmdir \"<emplacement d'origine>\"              (supprime la jonction seule)");
        text.AppendLine("  2. robocopy \"<copie>\" \"<emplacement d'origine>\" /E /COPYALL /DCOPY:DAT /XJ");
        text.AppendLine("  3. vérifie que le logiciel fonctionne, puis supprime la copie.");
        text.AppendLine();
        text.AppendLine($"Mis à jour le {DateTime.Now:dd/MM/yyyy HH:mm}.");
        text.AppendLine();
        foreach (var move in here)
        {
            text.AppendLine($"- {move.SoftwareName}");
            text.AppendLine($"    emplacement d'origine : {move.Source}");
            text.AppendLine($"    copie                 : {move.Destination}");
            text.AppendLine($"    opération             : {move.Move.Id}");
        }

        File.WriteAllText(Path.Combine(destinationRoot, FileName), text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}

/// <summary>
/// Logiciel FICTIF du mode « Démo sans risque » : un petit dossier factice (fichiers, sous-dossier, fichier caché,
/// lecture seule, flux ADS, script .cmd) créé dans un dossier bac à sable, pour essayer déplacement et annulation.
/// </summary>
public static class DemoSoftwareFactory
{
    public const string DemoFolderName = "Demenageur-Demo";
    public const string SoftwareName = "LogicielFictif";

    public static string SourceRoot(string driveRoot) => Path.Combine(driveRoot, DemoFolderName, "Programmes");

    public static string DestinationRoot(string driveRoot) => Path.Combine(driveRoot, DemoFolderName, "Destination");

    /// <summary>Crée (ou recrée si absent) le logiciel fictif ; renvoie son dossier.</summary>
    public static string Create(string driveRoot)
    {
        var folder = Path.Combine(SourceRoot(driveRoot), SoftwareName);
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            return folder;
        }

        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(Path.Combine(folder, "donnees"));
        var random = new Random(2026);
        void Bytes(string relative, int size)
        {
            var data = new byte[size];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(folder, relative), data);
        }

        Bytes(@"bin\moteur.dll", 400_000);
        Bytes(@"donnees\ressources.pak", 2_000_000);
        File.WriteAllText(Path.Combine(folder, "config.ini"), "[LogicielFictif]\r\nlangue=fr\r\n");
        File.WriteAllText(Path.Combine(folder, "licence.txt"), "Logiciel fictif de démonstration de Déménageur.");
        File.SetAttributes(Path.Combine(folder, "licence.txt"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(folder, "cache.dat"), "cache");
        File.SetAttributes(Path.Combine(folder, "cache.dat"), FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(folder, "LogicielFictif.cmd"),
            "@echo off\r\nchcp 65001 > nul\r\necho Logiciel fictif de Déménageur\r\necho Lancé depuis : %~dp0\r\necho.\r\ndir \"%~dp0\" /a\r\npause\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "LogicielFictif.cmd") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        return folder;
    }
}
