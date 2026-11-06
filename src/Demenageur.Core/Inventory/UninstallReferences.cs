using Demenageur.Core.FileSystem;

namespace Demenageur.Core.Inventory;

/// <summary>
/// Entrées de désinstallation qui désignent encore un dossier (InstallLocation, icône ou commande de désinstallation
/// dedans). Sert à savoir si un logiciel déplacé est toujours installé avant de proposer de nettoyer sa copie.
/// Lecture seule.
/// </summary>
public static class UninstallReferences
{
    public static IReadOnlyList<UninstallEntry> PointingInto(IEnumerable<UninstallEntry> entries, string folder)
        => entries.Where(e => !e.IsUpdate && References(e, folder)).ToList();

    public static bool References(UninstallEntry entry, string folder)
        => IsInside(Clean(entry.InstallLocation), folder)
           || IsInside(InstallFolderResolver.ExtractExecutablePath(entry.DisplayIcon), folder)
           || IsInside(InstallFolderResolver.ExtractExecutablePath(entry.UninstallString), folder);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"').Trim();

    private static bool IsInside(string? path, string folder)
        => path is not null && path.Length >= 3 && path[1] == ':' && PathUtil.IsWithinIgnoreCase(path, folder);
}
