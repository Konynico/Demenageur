namespace Demenageur.Core.Inventory;

/// <summary>
/// Construit la liste des logiciels à partir des entrées de désinstallation : résolution du dossier,
/// regroupement des entrées qui désignent le même produit dans le même dossier (vues 32/64 bits, HKLM/HKCU),
/// mises à jour et correctifs écartés.
/// </summary>
public sealed class SoftwareCatalog
{
    private readonly IUninstallSource _source;
    private readonly InstallFolderResolver _resolver;

    public SoftwareCatalog(IUninstallSource source, InstallFolderResolver resolver)
    {
        _source = source;
        _resolver = resolver;
    }

    /// <summary>Entrées brutes (lecture seule du registre).</summary>
    public IReadOnlyList<UninstallEntry> ReadEntries() => _source.ReadAll();

    public IReadOnlyList<InstalledSoftware> Load()
    {
        var resolved = _source.ReadAll()
            .Where(e => !e.IsUpdate && !string.IsNullOrWhiteSpace(e.DisplayName))
            .Select(e => (Entry: e, Folder: _resolver.Resolve(e)))
            .ToList();

        return resolved
            .GroupBy(x => (Name: x.Entry.DisplayName.Trim().ToUpperInvariant(), Folder: x.Folder.Path?.ToUpperInvariant() ?? x.Entry.RegistryKey.ToUpperInvariant()))
            .Select(group =>
            {
                var best = group.OrderBy(x => x.Entry.SystemComponent).ThenBy(x => x.Folder.Source == FolderSource.InstallLocation ? 0 : 1).First();
                var entries = group.Select(x => x.Entry).ToList();
                return new InstalledSoftware
                {
                    Id = best.Entry.RegistryKey,
                    DisplayName = best.Entry.DisplayName.Trim(),
                    Publisher = entries.Select(e => e.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim(),
                    Version = entries.Select(e => e.DisplayVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim(),
                    Folder = best.Folder,
                    Entries = entries,
                    IconPath = InstallFolderResolver.ExtractExecutablePath(entries.Select(e => e.DisplayIcon).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))),
                    EstimatedSizeKb = entries.Select(e => e.EstimatedSizeKb).FirstOrDefault(s => s is > 0),
                };
            })
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
