namespace Demenageur.Core.FileSystem;

/// <summary>Catégorie d'erreur disque, indépendante des codes Win32.</summary>
public enum FsErrorKind
{
    Other,
    NotFound,
    AlreadyExists,
    AccessDenied,
    SharingViolation,
    DirectoryNotEmpty,
    DiskFull,
    NotSameDevice,
    /// <summary>L'élément trouvé n'est pas celui attendu (identité différente : substitution possible).</summary>
    IdentityMismatch,
    /// <summary>Chemin refusé par la garde de périmètre.</summary>
    PathRejected,
    Cancelled,
    /// <summary>Erreur de lecture des données (CRC…).</summary>
    DataError,
    InvalidName,
    NotSupported,
    PrivilegeNotHeld,
}

/// <summary>Erreur disque typée.</summary>
public class FileSystemException : IOException
{
    public FileSystemException(FsErrorKind kind, string message, string? path = null, int win32Error = 0, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Path = path;
        Win32Error = win32Error;
        if (win32Error != 0)
        {
            HResult = unchecked((int)0x80070000 | win32Error);
        }
    }

    public FsErrorKind Kind { get; }

    public string? Path { get; }

    public int Win32Error { get; }

    /// <summary>Erreur probablement passagère (antivirus, indexeur) : un nouvel essai a du sens.</summary>
    public bool IsTransient => Kind is FsErrorKind.SharingViolation or FsErrorKind.AccessDenied;
}

/// <summary>Opération refusée par la garde de périmètre (<see cref="PathGuard"/>).</summary>
public sealed class PathGuardViolationException : FileSystemException
{
    public PathGuardViolationException(string message, string? path)
        : base(FsErrorKind.PathRejected, message, path)
    {
    }
}
