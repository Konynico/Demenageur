using Demenageur.Core.FileSystem;
using Demenageur.Core.Inventory;

namespace Demenageur.Core.Safety;

/// <summary>Ce que l'on veut déplacer : un dossier, et les entrées du registre qui le concernent.</summary>
public sealed record MoveCandidate(string Folder, string DisplayName, string? Publisher, IReadOnlyList<UninstallEntry> Entries, FolderSource FolderSource)
{
    public bool IsManualFolder => FolderSource == FolderSource.Manual;
}

/// <summary>
/// Liste noire à deux niveaux (décidée avec l'utilisateur) et avertissements.
/// <list type="bullet">
/// <item><b>Bloqué</b> : Windows, racines, dossiers personnels, WindowsApps, composants de Windows, pilotes,
/// sécurité, points d'accroche système, l'app elle-même.</item>
/// <item><b>Confirmation renforcée</b> : runtimes partagés, services, Common Files, entrées masquées.</item>
/// <item><b>Avertissement</b> : tâches planifiées, extensions/COM, dossiers voisins, entrée HKCU hors profil, dossier déduit.</item>
/// </list>
/// Chaque chemin système référencé est comparé sous sa forme réelle (8.3 et <c>\??\</c> résolus).
/// </summary>
public sealed class SafetyRules
{
    private static readonly string[] RuntimeNames =
    [
        "Microsoft .NET", ".NET Runtime", ".NET Desktop Runtime", "Windows Desktop Runtime", "ASP.NET Core", "Microsoft Visual C++",
        "Visual C++ Redistributable", "Java(TM)", "Java SE", "Java 8", "OpenJDK", "Eclipse Temurin", "Microsoft Build of OpenJDK",
        "DirectX", "Microsoft XNA", "Microsoft GameInput", "Visual Studio Tools for Office Runtime", "Microsoft Access database engine",
    ];

    private static readonly string[] WindowsComponentNames =
    [
        "Microsoft Edge", "Microsoft Edge Update", "Microsoft Edge WebView2", "Microsoft Update Health Tools", "Windows PC Health Check",
        "Update for Windows", "Windows Software Development Kit", "Microsoft OneDrive Setup",
    ];

    private static readonly string[] WindowsComponentFolders =
    [
        "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows NT",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "WindowsPowerShell", "Windows Sidebar",
        "Internet Explorer", "Microsoft Update Health Tools", "PCHealthCheck", "ModifiableWindowsApps", "WindowsApps",
        @"Microsoft\Edge", @"Microsoft\EdgeUpdate", @"Microsoft\EdgeCore", @"Microsoft\EdgeWebView", "Reference Assemblies",
    ];

    private static readonly string[] RuntimeFolders = ["dotnet", "Microsoft.NET", "Java", "Eclipse Adoptium", "Microsoft Visual Studio\\Shared"];

    private static readonly string[] SecurityVendors =
    [
        "Kaspersky", "ESET", "Bitdefender", "Norton", "Symantec", "McAfee", "Avast", "AVG Technologies", "Avira", "Sophos", "Trend Micro",
        "Malwarebytes", "Webroot", "F-Secure", "G DATA", "Panda Security", "Comodo", "CrowdStrike", "SentinelOne", "Cylance",
        "Windows Defender", "Microsoft Defender", "Emsisoft", "Bullguard", "Total AV", "TotalAV", "Dr.Web", "Quick Heal", "K7 Computing",
        "VIPRE", "ZoneAlarm", "Check Point", "Acronis Cyber Protect", "Cisco Secure Endpoint", "Trellix", "Elastic Endpoint", "Huorong",
    ];

    private readonly IKnownPaths _known;
    private readonly IFileSystem _fs;

    public SafetyRules(IKnownPaths known, IFileSystem fs)
    {
        _known = known;
        _fs = fs;
    }

    public IReadOnlyList<RuleVerdict> Evaluate(MoveCandidate candidate, SystemSnapshot system, IReadOnlyList<InstalledSoftware> catalog)
    {
        var verdicts = new List<RuleVerdict>();
        var folder = Real(candidate.Folder);
        bool Inside(string? path) => path is not null && PathUtil.IsWithinIgnoreCase(Real(path), folder);
        bool IsOrContains(string path) => PathUtil.IsWithinIgnoreCase(Real(path), folder);
        bool IsWithin(string root) => PathUtil.IsWithinIgnoreCase(folder, Real(root));

        // ─── Bloqué ───
        if (IsWithin(_known.WindowsDirectory) || IsOrContains(_known.WindowsDirectory))
        {
            Add(verdicts, "R-WINDOWS", RuleLevel.Block, "Dossier de Windows", "Le dossier de Windows et ses sous-dossiers ne sont jamais déplacés.");
        }

        var roots = new[] { _known.ProgramFiles, _known.ProgramFilesX86, _known.ProgramData, _known.UsersDirectory, _known.CommonProgramFiles, _known.CommonProgramFilesX86 }
            .Concat(_known.UserProfiles)
            .Concat(_known.UserProfiles.SelectMany(p => new[] { @"AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Programs" }.Select(s => PathUtil.Join(p, s))));
        if (PathUtil.IsDriveRoot(folder.Length == 2 ? folder + "\\" : folder) || roots.Any(IsOrContains))
        {
            Add(verdicts, "R-RACINE", RuleLevel.Block, "Dossier racine du système", "Ce dossier est (ou contient) une racine du système : Program Files, ProgramData, Users, un profil ou un lecteur entier.");
        }

        if (_known.PersonalFolders.Any(p => IsOrContains(p) || (candidate.IsManualFolder && IsWithin(p) && p.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))))
        {
            Add(verdicts, "R-PERSO", RuleLevel.Block, "Dossier personnel", "Les dossiers personnels (Documents, Bureau, Images, OneDrive…) et leurs parents ne sont pas déplacés par Déménageur.");
        }

        foreach (var component in WindowsComponentFolders)
        {
            foreach (var root in new[] { _known.ProgramFiles, _known.ProgramFilesX86 })
            {
                var componentPath = PathUtil.Join(root, component);
                if (IsWithin(componentPath) || IsOrContains(componentPath))
                {
                    Add(verdicts, "R-COMPOSANT-WINDOWS", RuleLevel.Block, "Composant de Windows", $"« {component} » fait partie de Windows (ou du runtime .NET partagé).");
                }
            }
        }

        if (candidate.Entries.Any(e => WindowsComponentNames.Any(n => e.DisplayName.StartsWith(n, StringComparison.OrdinalIgnoreCase))))
        {
            Add(verdicts, "R-COMPOSANT-WINDOWS", RuleLevel.Block, "Composant de Windows", "Ce produit est un composant de Windows (Edge, WebView2, outils de mise à jour…).");
        }

        if (IsWithin(PathUtil.Join(_known.ProgramData, "Package Cache")))
        {
            Add(verdicts, "R-CACHE", RuleLevel.Block, "Cache d'installation", "Les caches d'installation de Windows ne sont pas déplacés.");
        }

        foreach (var driver in system.Services.Where(s => s.IsDriver && Inside(s.ImagePath)))
        {
            Add(verdicts, "R-PILOTE", RuleLevel.Block, "Pilote", $"Le pilote « {driver.DisplayName} » ({driver.Name}) est chargé depuis ce dossier.");
        }

        foreach (var product in system.SecurityProducts.Where(p => p.Paths.Any(Inside)))
        {
            Add(verdicts, "R-SECURITE", RuleLevel.Block, "Logiciel de sécurité", $"« {product.Name} » est un logiciel de sécurité actif : le déplacer pourrait désactiver la protection de l'ordinateur.");
        }

        if (candidate.Entries.Any(e => IsSecurityVendor(e.Publisher) || IsSecurityVendor(e.DisplayName)) || IsSecurityVendor(candidate.Publisher))
        {
            Add(verdicts, "R-SECURITE", RuleLevel.Block, "Logiciel de sécurité", "L'éditeur est un éditeur de logiciels de sécurité (antivirus, pare-feu, EDR).");
        }

        foreach (var hook in system.SystemHooks.Where(h => Inside(h.Path)))
        {
            Add(verdicts, "R-ACCROCHE", RuleLevel.Block, "Point d'accroche système", $"{hook.Kind} : {hook.Description} est chargé depuis ce dossier (ouverture de session, sécurité, impression…).");
        }

        if (IsWithin(_known.ApplicationDirectory) || IsOrContains(_known.ApplicationDirectory) || IsWithin(_known.ApplicationDataDirectory) || IsOrContains(_known.ApplicationDataDirectory))
        {
            Add(verdicts, "R-APP", RuleLevel.Block, "Déménageur lui-même", "Déménageur ne se déplace pas lui-même, ni ses données.");
        }

        if (system.PendingRenames.Any(p => Inside(p) || PathUtil.IsWithinIgnoreCase(folder, p)))
        {
            Add(verdicts, "R-REDEMARRAGE", RuleLevel.Block, "Redémarrage en attente", "Une installation ou mise à jour attend un redémarrage pour remplacer des fichiers de ce dossier. Redémarre l'ordinateur d'abord.");
        }

        // Autres logiciels installés DANS un sous-dossier : ils seraient déplacés aussi.
        var nested = catalog.Where(s => s.Folder.Path is { } p && !PathUtil.IsWithinIgnoreCase(folder, p) && PathUtil.IsWithinIgnoreCase(p, folder)).ToList();
        if (nested.Count > 0)
        {
            Add(verdicts, "R-AUTRES-LOGICIELS", RuleLevel.Block, "Dossier partagé avec d'autres logiciels",
                "Ce dossier contient aussi : " + string.Join(", ", nested.Select(s => s.DisplayName).Take(8)) + (nested.Count > 8 ? "…" : string.Empty) + ". Déplace plutôt chaque logiciel séparément.");
        }

        // ─── Confirmation renforcée ───
        if (candidate.Entries.Any(e => RuntimeNames.Any(n => e.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase)))
            || RuntimeFolders.Any(r => new[] { _known.ProgramFiles, _known.ProgramFilesX86 }.Any(root => IsWithin(PathUtil.Join(root, r)) || IsOrContains(PathUtil.Join(root, r)))))
        {
            Add(verdicts, "R-RUNTIME", RuleLevel.Confirm, "Runtime partagé", "D'autres logiciels dépendent de ce runtime : s'il devenait inaccessible, ils ne démarreraient plus.");
        }

        var services = system.Services.Where(s => !s.IsDriver && (Inside(s.ImagePath) || Inside(s.ServiceDll))).ToList();
        if (services.Count > 0)
        {
            Add(verdicts, "R-SERVICE", RuleLevel.Confirm, "Service Windows",
                "Ce logiciel installe un ou plusieurs services : " + string.Join(", ", services.Select(s => s.DisplayName).Take(6)) + ". Ils devront être arrêtés pendant le déplacement et démarreront depuis le disque de destination.");
        }

        if (IsWithin(_known.CommonProgramFiles) || IsWithin(_known.CommonProgramFilesX86))
        {
            Add(verdicts, "R-COMMON-FILES", RuleLevel.Confirm, "Composants partagés", "Les sous-dossiers de Common Files sont partagés entre plusieurs logiciels d'un même éditeur.");
        }

        if (candidate.Entries.Count > 0 && candidate.Entries.All(e => e.SystemComponent))
        {
            Add(verdicts, "R-MASQUE", RuleLevel.Confirm, "Composant masqué", "Ce produit est masqué de « Programmes et fonctionnalités » : c'est en général un sous-composant d'un autre logiciel.");
        }

        // ─── Avertissements ───
        var tasks = system.ScheduledTasks.Where(t => t.Definition.Contains(folder, StringComparison.OrdinalIgnoreCase) || t.Definition.Contains(candidate.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tasks.Count > 0)
        {
            Add(verdicts, "R-TACHE", RuleLevel.Warning, "Tâches planifiées", "Tâches qui lancent ce logiciel : " + string.Join(", ", tasks.Select(t => t.Name).Take(6)) + ". Elles continueront de fonctionner via la jonction.");
        }

        var com = system.ComServers.Where(c => Inside(c.Path)).ToList();
        if (com.Count > 0)
        {
            Add(verdicts, "R-COM", RuleLevel.Warning, "Extensions et composants COM", $"{com.Count} composant(s) COM ou extension(s) de l'Explorateur sont fournis par ce logiciel : l'Explorateur peut retenir ces fichiers.");
        }

        if (!candidate.IsManualFolder && PathUtil.GetParent(folder) is { } parent && !IsKnownRootFolder(parent)
            && catalog.Any(s => s.Folder.Path is { } p && !string.Equals(Real(p), folder, StringComparison.OrdinalIgnoreCase) && string.Equals(PathUtil.GetParent(Real(p)), parent, StringComparison.OrdinalIgnoreCase)))
        {
            Add(verdicts, "R-VOISINS", RuleLevel.Warning, "Dossiers voisins du même éditeur",
                $"« {parent} » contient d'autres logiciels. Si ce logiciel les utilise via un chemin relatif (..\\), il pourrait ne plus les trouver : envisage de déplacer tout le dossier de l'éditeur.");
        }

        foreach (var entry in candidate.Entries.Where(e => e.Scope == RegistryScope.User && e.UserProfile is not null && !PathUtil.IsWithinIgnoreCase(folder, e.UserProfile)))
        {
            Add(verdicts, "R-HKCU", RuleLevel.Warning, "Entrée d'utilisateur hors de son profil", $"L'entrée « {entry.DisplayName} » d'un profil utilisateur désigne un dossier hors de ce profil : vérifie qu'il s'agit bien du bon logiciel.");
        }

        if (candidate.FolderSource is FolderSource.DisplayIcon or FolderSource.UninstallString)
        {
            Add(verdicts, "R-DEDUIT", RuleLevel.Warning, "Dossier déduit", "Le registre n'indique pas le dossier d'installation : il a été déduit. Vérifie qu'il s'agit bien du dossier du logiciel.");
        }

        return verdicts;
    }

    private bool IsKnownRootFolder(string path)
        => new[] { _known.ProgramFiles, _known.ProgramFilesX86, _known.ProgramData }
            .Concat(_known.UserProfiles.Select(p => PathUtil.Join(p, @"AppData\Local\Programs")))
            .Concat(_known.UserProfiles.Select(p => PathUtil.Join(p, @"AppData\Local")))
            .Any(r => string.Equals(PathUtil.TrimTrailingSeparator(r), PathUtil.TrimTrailingSeparator(path), StringComparison.OrdinalIgnoreCase))
           || PathUtil.IsDriveRoot(path);

    private static bool IsSecurityVendor(string? text)
        => text is not null && SecurityVendors.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>Forme réelle d'un chemin (noms 8.3 développés, préfixes \??\ et \\?\ retirés) quand il existe.</summary>
    private string Real(string path)
    {
        var cleaned = path.Trim().Trim('"');
        if (cleaned.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            cleaned = cleaned[4..];
        }

        cleaned = PathUtil.TrimTrailingSeparator(PathUtil.StripExtendedPrefix(cleaned));
        if (!PathUtil.TryValidateLocalPath(cleaned, out var normalized, out _))
        {
            return cleaned;
        }

        try
        {
            return _fs.GetEntry(normalized) is not null ? PathUtil.TrimTrailingSeparator(_fs.GetFinalPath(normalized, followFinalLink: false)) : normalized;
        }
        catch (FileSystemException)
        {
            return normalized;
        }
    }

    private static void Add(List<RuleVerdict> verdicts, string id, RuleLevel level, string title, string explanation)
    {
        if (!verdicts.Any(v => v.RuleId == id && v.Explanation == explanation))
        {
            verdicts.Add(new RuleVerdict(id, level, title, explanation));
        }
    }
}
