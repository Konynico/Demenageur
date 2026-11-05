namespace Demenageur.Core.FileSystem;

/// <summary>
/// Seul point d'accès au disque de Déménageur. Toutes les opérations :
/// <list type="bullet">
/// <item>utilisent des chemins locaux complets (le préfixe <c>\\?\</c> est ajouté par l'implémentation) ;</item>
/// <item>ne suivent JAMAIS un point d'analyse final, sauf mention contraire ;</item>
/// <item>vérifient, quand une identité attendue est fournie, que l'élément ouvert est bien celui attendu
/// (sinon <see cref="FsErrorKind.IdentityMismatch"/>), et agissent ensuite via ce handle.</item>
/// </list>
/// </summary>
public interface IFileSystem
{
    // ───────────── Lecture ─────────────

    /// <summary>Informations sur l'élément, sans suivre un point d'analyse final. Null si absent.</summary>
    FsEntryInfo? GetEntry(string path);

    /// <summary>Informations sur l'élément en suivant les liens (cible d'une jonction). Null si absent ou cible absente.</summary>
    FsEntryInfo? GetEntryFollowingLinks(string path);

    /// <summary>
    /// Liste un dossier (sans « . » ni « .. »). Le dossier est ouvert sans suivre de point d'analyse
    /// (ou en le suivant si <paramref name="followLink"/>) et son identité est vérifiée avant l'énumération.
    /// </summary>
    IReadOnlyList<DirectoryChild> ListDirectory(string directoryPath, FileIdentity expectedIdentity, bool followLink = false);

    /// <summary>Lit le point d'analyse (jonction, lien) de l'élément, identité vérifiée.</summary>
    ReparsePointData ReadReparsePoint(string path, FileIdentity expectedIdentity);

    /// <summary>Flux de données nommés (ADS) d'un fichier, hors flux principal.</summary>
    IReadOnlyList<AlternateStream> ListAlternateStreams(string filePath, FileIdentity expectedIdentity);

    /// <summary>
    /// Calcule l'empreinte XxHash128 d'un flux (<paramref name="streamName"/> null = flux principal).
    /// Avec <paramref name="unbuffered"/>, la lecture contourne le cache système (relecture réelle du disque).
    /// </summary>
    ContentHash HashStream(string filePath, string? streamName, FileIdentity expectedIdentity, bool unbuffered, Action<long>? bytesRead, CancellationToken cancellationToken);

    /// <summary>Descripteur de sécurité (propriétaire, groupe, DACL) au format binaire auto-relatif.</summary>
    byte[] GetSecurityDescriptor(string path, FileIdentity expectedIdentity);

    /// <summary>Chemin réel canonique (noms longs, jonctions des dossiers parents résolues), sans préfixe <c>\\?\</c>.</summary>
    string GetFinalPath(string path, bool followFinalLink);

    /// <summary>Informations sur le volume qui contient le chemin (qui peut ne pas encore exister).</summary>
    VolumeInfo GetVolumeInfo(string path);

    /// <summary>Lit entièrement un petit fichier de données de l'app (journal, manifeste). Null si absent.</summary>
    byte[]? ReadAllBytes(string path);

    /// <summary>Vérifie qu'un fichier peut être ouvert en lecture (sans le lire) ; lève une erreur sinon.</summary>
    void ProbeRead(string filePath, FileIdentity expectedIdentity);

    // ───────────── Modifications ─────────────

    /// <summary>Crée un dossier ; échoue (<see cref="FsErrorKind.AlreadyExists"/>) s'il existe déjà. Descripteur facultatif posé à la création.</summary>
    FileIdentity CreateDirectory(string path, byte[]? securityDescriptor = null);

    /// <summary>Copie un fichier (données, flux ADS, attributs étendus) vers un nouveau fichier ; échoue si la destination existe.</summary>
    FileIdentity CopyFile(string sourcePath, FileIdentity expectedSourceIdentity, string destinationPath, CopyFileOptions options, Action<long>? bytesCopied, CancellationToken cancellationToken);

    /// <summary>Crée un nouveau nom (lien physique) pour un fichier existant.</summary>
    FileIdentity CreateHardLink(string newLinkPath, string existingFilePath, FileIdentity expectedExistingIdentity);

    /// <summary>Crée un dossier (ou fichier) vide puis y pose le point d'analyse donné, octet pour octet.</summary>
    FileIdentity CreateReparsePoint(string path, bool isDirectory, ReparsePointData data);

    /// <summary>Renomme l'élément dans son dossier parent (jamais de remplacement, jamais de changement de volume).</summary>
    void Rename(string path, FileIdentity expectedIdentity, string newName);

    /// <summary>Pose horodatages et attributs (0 = inchangé).</summary>
    void SetBasicInfo(string path, FileIdentity expectedIdentity, FileTimestamps times, FileAttributes attributes);

    /// <summary>Pose le descripteur de sécurité tel quel (sans recalcul d'héritage ni propagation aux enfants).</summary>
    void SetSecurityDescriptor(string path, FileIdentity expectedIdentity, byte[] securityDescriptor, SecurityParts parts);

    /// <summary>Pose le nom court 8.3 ; renvoie faux si impossible (privilège absent, volume sans noms courts…).</summary>
    bool TrySetShortName(string path, FileIdentity expectedIdentity, string shortName);

    /// <summary>
    /// Supprime l'élément par son handle (sémantique POSIX, attribut lecture seule ignoré).
    /// Un lien est supprimé lui-même, jamais sa cible. Un dossier doit être vide.
    /// </summary>
    void Delete(string path, FileIdentity expectedIdentity);

    /// <summary>Vide les tampons d'écriture d'un fichier sur le disque.</summary>
    void FlushFile(string path, FileIdentity expectedIdentity);

    /// <summary>Vide les tampons de tout le volume (droits administrateur). Faux si impossible.</summary>
    bool TryFlushVolume(string path);

    /// <summary>Ajoute des octets en fin de fichier (créé si absent), écriture directe sur disque.</summary>
    void AppendToFile(string path, ReadOnlySpan<byte> data);

    /// <summary>Écrit un nouveau fichier (échoue s'il existe) et le vide sur disque.</summary>
    void WriteNewFile(string path, ReadOnlySpan<byte> data);
}
