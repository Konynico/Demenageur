using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Demenageur.Core.FileSystem;
using Demenageur.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using static Demenageur.Windows.Interop.NativeMethods;

namespace Demenageur.Windows.FileSystem;

/// <summary>
/// Implémentation réelle de <see cref="IFileSystem"/> par l'API Win32.
/// Tous les chemins reçoivent le préfixe <c>\\?\</c> (noms longs, noms finissant par un point ou une espace, <c>CON</c>…).
/// </summary>
public sealed unsafe class Win32FileSystem : IFileSystem
{
    private const int ReadBufferSize = 1 << 20; // 1 Mio, multiple de toute taille de secteur.
    private const int SectorAlignment = 4096;

    // ───────────────────────── Utilitaires ─────────────────────────

    internal static string Ext(string path)
    {
        if (!PathUtil.TryValidateLocalPath(path, out var normalized, out var error))
        {
            throw new FileSystemException(FsErrorKind.InvalidName, $"Chemin invalide ({error}) : « {path} ».", path);
        }

        return PathUtil.ExtendedPrefix + normalized;
    }

    internal static FileSystemException Error(int win32Error, string operation, string? path)
    {
        var kind = win32Error switch
        {
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_DRIVE or ERROR_NOT_READY => FsErrorKind.NotFound,
            ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS => FsErrorKind.AlreadyExists,
            ERROR_ACCESS_DENIED or ERROR_DELETE_PENDING => FsErrorKind.AccessDenied,
            ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => FsErrorKind.SharingViolation,
            ERROR_DIR_NOT_EMPTY => FsErrorKind.DirectoryNotEmpty,
            ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL => FsErrorKind.DiskFull,
            ERROR_NOT_SAME_DEVICE => FsErrorKind.NotSameDevice,
            ERROR_CRC => FsErrorKind.DataError,
            ERROR_INVALID_NAME => FsErrorKind.InvalidName,
            ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION => FsErrorKind.NotSupported,
            ERROR_PRIVILEGE_NOT_HELD => FsErrorKind.PrivilegeNotHeld,
            ERROR_REQUEST_ABORTED or ERROR_CANCELLED or ERROR_OPERATION_ABORTED => FsErrorKind.Cancelled,
            _ => FsErrorKind.Other,
        };
        var message = new System.ComponentModel.Win32Exception(win32Error).Message;
        return new FileSystemException(kind, $"{operation} « {path} » : {message} (code {win32Error}).", path, win32Error);
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags, string operation)
    {
        var handle = CreateFile(Ext(path), access, share, 0, disposition, flags, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Error(error, operation, path);
        }

        return handle;
    }

    private static SafeFileHandle OpenNoFollow(string path, uint access, string operation)
        => Open(path, access, FILE_SHARE_ALL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, operation);

    internal static FileIdentity QueryIdentity(SafeFileHandle handle, string path)
    {
        FILE_ID_INFO info;
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, &info, (uint)sizeof(FILE_ID_INFO)))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Lecture de l'identité de", path);
        }

        return new FileIdentity(info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow));
    }

    private static void VerifyIdentity(SafeFileHandle handle, FileIdentity expected, string path)
    {
        var actual = QueryIdentity(handle, path);
        if (actual != expected)
        {
            throw new FileSystemException(
                FsErrorKind.IdentityMismatch,
                $"L'élément « {path} » n'est plus celui attendu (identité {actual} au lieu de {expected}) : opération annulée par sécurité.",
                path);
        }
    }

    private static long FileTime(uint high, uint low) => (long)(((ulong)high << 32) | low);

    private static FsEntryInfo BuildInfo(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var basic))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Lecture des informations de", path);
        }

        var attributes = (FileAttributes)basic.FileAttributes;
        uint tag = 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            FILE_ATTRIBUTE_TAG_INFO tagInfo;
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &tagInfo, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
            {
                throw Error(Marshal.GetLastPInvokeError(), "Lecture du point d'analyse de", path);
            }

            tag = tagInfo.ReparseTag;
        }

        var caseSensitive = false;
        if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
        {
            uint flags;
            if (GetFileInformationByHandleEx(handle, FileCaseSensitiveInfo, &flags, sizeof(uint)))
            {
                caseSensitive = (flags & FILE_CS_FLAG_CASE_SENSITIVE_DIR) != 0;
            }
        }

        return new FsEntryInfo
        {
            Path = path,
            Attributes = attributes,
            ReparseTag = tag,
            Size = (attributes & FileAttributes.Directory) != 0 ? 0 : (long)(((ulong)basic.FileSizeHigh << 32) | basic.FileSizeLow),
            Times = new FileTimestamps(
                FileTime(basic.CreationTimeHigh, basic.CreationTimeLow),
                FileTime(basic.LastAccessTimeHigh, basic.LastAccessTimeLow),
                FileTime(basic.LastWriteTimeHigh, basic.LastWriteTimeLow)),
            Identity = QueryIdentity(handle, path),
            LinkCount = basic.NumberOfLinks,
            IsCaseSensitiveDirectory = caseSensitive,
        };
    }

    private static bool IsNotFound(int error)
        => error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_INVALID_DRIVE or ERROR_NOT_READY or ERROR_DIRECTORY;

    // ───────────────────────── Lecture ─────────────────────────

    public FsEntryInfo? GetEntry(string path) => Stat(path, follow: false);

    public FsEntryInfo? GetEntryFollowingLinks(string path) => Stat(path, follow: true);

    private static FsEntryInfo? Stat(string path, bool follow)
    {
        var flags = FILE_FLAG_BACKUP_SEMANTICS | (follow ? 0 : FILE_FLAG_OPEN_REPARSE_POINT);
        using var handle = CreateFile(Ext(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING, flags, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (IsNotFound(error))
            {
                return null;
            }

            throw Error(error, "Lecture des informations de", path);
        }

        return BuildInfo(handle, path);
    }

    public IReadOnlyList<DirectoryChild> ListDirectory(string directoryPath, FileIdentity expectedIdentity, bool followLink = false)
    {
        var flags = FILE_FLAG_BACKUP_SEMANTICS | (followLink ? 0 : FILE_FLAG_OPEN_REPARSE_POINT);
        using var handle = Open(directoryPath, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_ALL, OPEN_EXISTING, flags, "Lecture du dossier");
        VerifyIdentity(handle, expectedIdentity, directoryPath);

        var result = new List<DirectoryChild>();
        const int bufferSize = 64 * 1024;
        var buffer = (byte*)NativeMemory.AlignedAlloc(bufferSize, 8);
        try
        {
            var infoClass = FileIdBothDirectoryRestartInfo;
            while (true)
            {
                if (!GetFileInformationByHandleEx(handle, infoClass, buffer, bufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ERROR_NO_MORE_FILES)
                    {
                        break;
                    }

                    throw Error(error, "Énumération du dossier", directoryPath);
                }

                infoClass = FileIdBothDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    var entry = new ReadOnlySpan<byte>(buffer + offset, bufferSize - offset);
                    var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    var endOfFile = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
                    var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
                    var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);
                    var shortNameLength = entry[68];
                    var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry[96..]);
                    // Décodage sans perte (pas de remplacement des surrogates isolés).
                    var name = new string(MemoryMarshal.Cast<byte, char>(entry.Slice(104, nameLength)));
                    string? shortName = shortNameLength > 0
                        ? new string(MemoryMarshal.Cast<byte, char>(entry.Slice(70, shortNameLength)))
                        : null;
                    if (name is not ("." or ".."))
                    {
                        result.Add(new DirectoryChild(name, fileId, attributes, shortName, endOfFile));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    offset += (int)next;
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        return result;
    }

    public ReparsePointData ReadReparsePoint(string path, FileIdentity expectedIdentity)
    {
        using var handle = OpenNoFollow(path, FILE_READ_ATTRIBUTES, "Lecture du point d'analyse de");
        VerifyIdentity(handle, expectedIdentity, path);
        return ReadReparse(handle, path);
    }

    private static ReparsePointData ReadReparse(SafeFileHandle handle, string path)
    {
        var buffer = new byte[MAXIMUM_REPARSE_DATA_BUFFER_SIZE];
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, null, 0, p, (uint)buffer.Length, out var returned, 0))
            {
                throw Error(Marshal.GetLastPInvokeError(), "Lecture du point d'analyse de", path);
            }

            return ReparsePointData.Parse(buffer.AsSpan(0, (int)returned).ToArray());
        }
    }

    public IReadOnlyList<AlternateStream> ListAlternateStreams(string filePath, FileIdentity expectedIdentity)
    {
        using var handle = OpenNoFollow(filePath, FILE_READ_ATTRIBUTES, "Lecture des flux de");
        VerifyIdentity(handle, expectedIdentity, filePath);

        var size = 4096;
        while (true)
        {
            var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 8);
            try
            {
                if (!GetFileInformationByHandleEx(handle, FileStreamInfo, buffer, (uint)size))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER)
                    {
                        size *= 4;
                        continue;
                    }

                    if (error == ERROR_HANDLE_EOF)
                    {
                        return []; // aucun flux (cas d'un dossier)
                    }

                    throw Error(error, "Lecture des flux de", filePath);
                }

                var streams = new List<AlternateStream>();
                var offset = 0;
                while (true)
                {
                    var entry = new ReadOnlySpan<byte>(buffer + offset, size - offset);
                    var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                    var streamSize = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
                    var rawName = new string(MemoryMarshal.Cast<byte, char>(entry.Slice(24, nameLength)));

                    // Format « :nom:$DATA » ; « ::$DATA » est le flux principal.
                    if (rawName.EndsWith(":$DATA", StringComparison.Ordinal) && rawName.Length > ":$DATA".Length + 1)
                    {
                        streams.Add(new AlternateStream(rawName[1..^":$DATA".Length], streamSize));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    offset += (int)next;
                }

                return streams;
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }
    }

    public ContentHash HashStream(string filePath, string? streamName, FileIdentity expectedIdentity, bool unbuffered, Action<long>? bytesRead, CancellationToken cancellationToken)
    {
        var target = streamName is null ? filePath : filePath + ":" + streamName + ":$DATA";
        if (streamName is not null && (streamName.Length == 0 || streamName.IndexOfAny([':', '\\']) >= 0))
        {
            throw new FileSystemException(FsErrorKind.InvalidName, $"Nom de flux invalide : « {streamName} ».", filePath);
        }

        // Les fichiers sont ouverts en suivant les points d'analyse « données transparentes » (WOF) ;
        // si un lien avait été substitué, l'identité ne correspondrait plus.
        var flags = FILE_FLAG_SEQUENTIAL_SCAN | (unbuffered ? FILE_FLAG_NO_BUFFERING : 0);
        var handle = CreateFile(Ext(filePath) + (streamName is null ? string.Empty : ":" + streamName + ":$DATA"),
            GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, flags, 0);
        if (handle.IsInvalid && unbuffered && Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER)
        {
            handle.Dispose();
            return HashStream(filePath, streamName, expectedIdentity, unbuffered: false, bytesRead, cancellationToken);
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Error(error, "Lecture de", target);
        }

        using (handle)
        {
            VerifyIdentity(handle, expectedIdentity, filePath);
            var hasher = new XxHash128();
            var buffer = (byte*)NativeMemory.AlignedAlloc(ReadBufferSize, SectorAlignment);
            try
            {
                var span = new Span<byte>(buffer, ReadBufferSize);
                long offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try
                    {
                        read = RandomAccess.Read(handle, span, offset);
                    }
                    catch (IOException ex)
                    {
                        throw new FileSystemException(FsErrorKind.DataError, $"Erreur de lecture de « {target} » : {ex.Message}", target, ex.HResult & 0xFFFF, ex);
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    hasher.Append(span[..read]);
                    offset += read;
                    bytesRead?.Invoke(read);
                    if (read < ReadBufferSize)
                    {
                        break;
                    }
                }
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }

            return new ContentHash(hasher.GetCurrentHashAsUInt128());
        }
    }

    public byte[] GetSecurityDescriptor(string path, FileIdentity expectedIdentity)
    {
        using var handle = OpenNoFollow(path, READ_CONTROL | FILE_READ_ATTRIBUTES, "Lecture des permissions de");
        VerifyIdentity(handle, expectedIdentity, path);
        const uint info = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION;
        var size = 1024u;
        while (true)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (GetKernelObjectSecurity(handle, info, p, size, out var needed))
                {
                    return buffer.AsSpan(0, (int)Math.Min(needed == 0 ? size : needed, size)).ToArray();
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ERROR_INSUFFICIENT_BUFFER)
                {
                    throw Error(error, "Lecture des permissions de", path);
                }

                size = Math.Max(needed, size * 2);
            }
        }
    }

    public string GetFinalPath(string path, bool followFinalLink)
    {
        var flags = FILE_FLAG_BACKUP_SEMANTICS | (followFinalLink ? 0 : FILE_FLAG_OPEN_REPARSE_POINT);
        using var handle = Open(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, OPEN_EXISTING, flags, "Résolution du chemin de");
        var size = 1024u;
        while (true)
        {
            var buffer = new char[size];
            fixed (char* p = buffer)
            {
                var length = GetFinalPathNameByHandle(handle, p, size, 0);
                if (length == 0)
                {
                    throw Error(Marshal.GetLastPInvokeError(), "Résolution du chemin de", path);
                }

                if (length < size)
                {
                    return PathUtil.StripExtendedPrefix(new string(buffer, 0, (int)length));
                }

                size = length + 1;
            }
        }
    }

    public VolumeInfo GetVolumeInfo(string path)
    {
        var ext = Ext(path);
        var buffer = stackalloc char[1024];
        if (!GetVolumePathName(ext, buffer, 1024))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Recherche du volume de", path);
        }

        var root = PathUtil.StripExtendedPrefix(new string(buffer));
        if (!root.EndsWith('\\'))
        {
            root += "\\";
        }

        string? guidPath = null;
        var guidBuffer = stackalloc char[128];
        if (GetVolumeNameForVolumeMountPoint(root, guidBuffer, 128))
        {
            guidPath = new string(guidBuffer);
        }

        var label = stackalloc char[261];
        var fsName = stackalloc char[261];
        if (!GetVolumeInformation(root, label, 261, out _, out _, out var fsFlags, fsName, 261))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Lecture des informations du volume", root);
        }

        GetDiskFreeSpaceEx(root, out var freeAvailable, out var total, out _);
        long clusterSize = 4096;
        if (GetDiskFreeSpace(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
        {
            clusterSize = (long)sectorsPerCluster * bytesPerSector;
        }

        var driveType = GetDriveType(root) switch
        {
            1 => VolumeDriveType.NoRootDirectory,
            2 => VolumeDriveType.Removable,
            3 => VolumeDriveType.Fixed,
            4 => VolumeDriveType.Remote,
            5 => VolumeDriveType.CdRom,
            6 => VolumeDriveType.RamDisk,
            _ => VolumeDriveType.Unknown,
        };

        return new VolumeInfo
        {
            RootPath = root,
            VolumeGuidPath = guidPath,
            FileSystemName = new string(fsName),
            Label = new string(label),
            FileSystemFlags = fsFlags,
            ClusterSize = clusterSize,
            FreeBytesAvailable = (long)freeAvailable,
            TotalBytes = (long)total,
            DriveType = driveType,
        };
    }

    public void ProbeRead(string filePath, FileIdentity expectedIdentity)
    {
        using var handle = Open(filePath, GENERIC_READ, FILE_SHARE_ALL, OPEN_EXISTING, 0, "Ouverture en lecture de");
        VerifyIdentity(handle, expectedIdentity, filePath);
    }

    public byte[]? ReadAllBytes(string path)
    {
        var handle = CreateFile(Ext(path), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (IsNotFound(error))
            {
                return null;
            }

            throw Error(error, "Lecture de", path);
        }

        using (handle)
        {
            var length = RandomAccess.GetLength(handle);
            var data = new byte[length];
            var total = 0;
            while (total < length)
            {
                var read = RandomAccess.Read(handle, data.AsSpan(total), total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total == length ? data : data[..total];
        }
    }

    // ───────────────────────── Modifications ─────────────────────────

    public FileIdentity CreateDirectory(string path, byte[]? securityDescriptor = null)
    {
        bool ok;
        if (securityDescriptor is null)
        {
            ok = NativeMethods.CreateDirectory(Ext(path), null);
        }
        else
        {
            fixed (byte* sd = securityDescriptor)
            {
                var attributes = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = sd };
                ok = NativeMethods.CreateDirectory(Ext(path), &attributes);
            }
        }

        if (!ok)
        {
            throw Error(Marshal.GetLastPInvokeError(), "Création du dossier", path);
        }

        return (Stat(path, follow: false) ?? throw Error(ERROR_PATH_NOT_FOUND, "Création du dossier", path)).Identity;
    }

    private sealed class CopyState
    {
        public Action<long>? Progress;
        public long LastReported;
        public CancellationToken Token;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint CopyProgressRoutine(byte* message, nint context)
    {
        var state = (CopyState)GCHandle.FromIntPtr(context).Target!;
        if (state.Token.IsCancellationRequested)
        {
            return COPYFILE2_PROGRESS_CANCEL;
        }

        if (*(int*)message == COPYFILE2_CALLBACK_CHUNK_FINISHED && state.Progress is not null)
        {
            var total = *(long*)(message + 72);
            var delta = total - state.LastReported;
            state.LastReported = total;
            if (delta > 0)
            {
                state.Progress(delta);
            }
        }

        return COPYFILE2_PROGRESS_CONTINUE;
    }

    public FileIdentity CopyFile(string sourcePath, FileIdentity expectedSourceIdentity, string destinationPath, CopyFileOptions options, Action<long>? bytesCopied, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Stat(sourcePath, follow: false) ?? throw Error(ERROR_FILE_NOT_FOUND, "Copie de", sourcePath);
        if (source.Identity != expectedSourceIdentity)
        {
            throw new FileSystemException(FsErrorKind.IdentityMismatch, $"« {sourcePath} » n'est plus le fichier attendu : copie annulée.", sourcePath);
        }

        if (source.Kind != FsEntryKind.File)
        {
            throw new FileSystemException(FsErrorKind.NotSupported, $"« {sourcePath} » n'est pas un fichier ordinaire : copie refusée.", sourcePath);
        }

        var state = new CopyState { Progress = bytesCopied, Token = cancellationToken };
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            var parameters = new COPYFILE2_EXTENDED_PARAMETERS
            {
                dwSize = (uint)sizeof(COPYFILE2_EXTENDED_PARAMETERS),
                dwCopyFlags = COPY_FILE_FAIL_IF_EXISTS | COPY_FILE_DONT_REQUEST_DEST_WRITE_DAC
                              | ((options & CopyFileOptions.NoBuffering) != 0 ? COPY_FILE_NO_BUFFERING : 0),
                pProgressRoutine = &CopyProgressRoutine,
                pvCallbackContext = GCHandle.ToIntPtr(gcHandle),
            };
            var hr = CopyFile2(Ext(sourcePath), Ext(destinationPath), &parameters);
            if (hr < 0)
            {
                var win32 = (hr & 0x1FFF0000) == 0x00070000 ? hr & 0xFFFF : hr;
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw Error(win32, $"Copie de « {sourcePath} » vers", destinationPath);
            }
        }
        finally
        {
            gcHandle.Free();
        }

        return (Stat(destinationPath, follow: false) ?? throw Error(ERROR_FILE_NOT_FOUND, "Copie vers", destinationPath)).Identity;
    }

    public FileIdentity CreateHardLink(string newLinkPath, string existingFilePath, FileIdentity expectedExistingIdentity)
    {
        var existing = Stat(existingFilePath, follow: false) ?? throw Error(ERROR_FILE_NOT_FOUND, "Lien physique vers", existingFilePath);
        if (existing.Identity != expectedExistingIdentity || existing.Kind != FsEntryKind.File)
        {
            throw new FileSystemException(FsErrorKind.IdentityMismatch, $"« {existingFilePath} » n'est plus le fichier attendu.", existingFilePath);
        }

        if (!NativeMethods.CreateHardLink(Ext(newLinkPath), Ext(existingFilePath), 0))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Création du lien physique", newLinkPath);
        }

        return (Stat(newLinkPath, follow: false) ?? throw Error(ERROR_FILE_NOT_FOUND, "Lien physique", newLinkPath)).Identity;
    }

    public FileIdentity CreateReparsePoint(string path, bool isDirectory, ReparsePointData data)
    {
        if (isDirectory)
        {
            if (!NativeMethods.CreateDirectory(Ext(path), null))
            {
                throw Error(Marshal.GetLastPInvokeError(), "Création du dossier", path);
            }
        }
        else
        {
            using var created = Open(path, GENERIC_WRITE, 0, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, "Création du fichier");
        }

        var handle = OpenNoFollow(path, GENERIC_WRITE | FILE_READ_ATTRIBUTES | DELETE, "Pose du point d'analyse sur");
        using (handle)
        {
            fixed (byte* p = data.Raw)
            {
                if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, p, (uint)data.Raw.Length, null, 0, out _, 0))
                {
                    var error = Marshal.GetLastPInvokeError();
                    // On retire l'élément vide que l'on vient de créer.
                    MarkForDeletion(handle, path);
                    throw Error(error, "Pose du point d'analyse sur", path);
                }
            }

            return QueryIdentity(handle, path);
        }
    }

    public void Rename(string path, FileIdentity expectedIdentity, string newName)
    {
        using var handle = OpenNoFollow(path, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, "Renommage de");
        VerifyIdentity(handle, expectedIdentity, path);

        // Jamais de remplacement : on le signale clairement (le système refuserait de toute façon).
        var parent = PathUtil.GetParent(path) ?? throw Error(ERROR_INVALID_NAME, "Renommage de", path);
        var targetPath = PathUtil.Join(parent, newName);
        if (Stat(targetPath, follow: false) is not null)
        {
            throw new FileSystemException(FsErrorKind.AlreadyExists, $"Renommage de « {path} » impossible : « {newName} » existe déjà.", path, ERROR_ALREADY_EXISTS);
        }

        // Chemin complet de la cible (un nom seul serait interprété par rapport au dossier courant du processus).
        var fullTarget = Ext(targetPath);

        // FILE_RENAME_INFO : Flags (4) + pad (4) + RootDirectory (8) + FileNameLength (4) + FileName.
        var nameBytes = fullTarget.Length * sizeof(char);
        var size = 20 + nameBytes + sizeof(char);
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 8);
        try
        {
            new Span<byte>(buffer, size).Clear();
            *(uint*)(buffer + 16) = (uint)nameBytes;
            fixed (char* name = fullTarget)
            {
                Buffer.MemoryCopy(name, buffer + 20, nameBytes, nameBytes);
            }

            // Renommage dans le même dossier, sans remplacement : atomique, et impossible si un fichier du dossier est ouvert.
            if (!SetFileInformationByHandle(handle, FileRenameInfo, buffer, (uint)size))
            {
                throw Error(Marshal.GetLastPInvokeError(), $"Renommage en « {newName} » de", path);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    public void SetBasicInfo(string path, FileIdentity expectedIdentity, FileTimestamps times, FileAttributes attributes)
    {
        using var handle = OpenNoFollow(path, FILE_WRITE_ATTRIBUTES | FILE_READ_ATTRIBUTES, "Modification des attributs de");
        VerifyIdentity(handle, expectedIdentity, path);
        var info = new FILE_BASIC_INFO
        {
            CreationTime = times.CreationTime,
            LastAccessTime = times.LastAccessTime,
            LastWriteTime = times.LastWriteTime,
            ChangeTime = 0,
            FileAttributes = (uint)attributes,
        };
        if (!SetFileInformationByHandle(handle, FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Modification des attributs de", path);
        }
    }

    public void SetSecurityDescriptor(string path, FileIdentity expectedIdentity, byte[] securityDescriptor, SecurityParts parts)
    {
        uint access = READ_CONTROL | FILE_READ_ATTRIBUTES;
        uint info = 0;
        if ((parts & SecurityParts.Dacl) != 0)
        {
            access |= WRITE_DAC;
            info |= DACL_SECURITY_INFORMATION;
        }

        if ((parts & (SecurityParts.Owner | SecurityParts.Group)) != 0)
        {
            access |= WRITE_OWNER;
        }

        if ((parts & SecurityParts.Owner) != 0)
        {
            info |= OWNER_SECURITY_INFORMATION;
        }

        if ((parts & SecurityParts.Group) != 0)
        {
            info |= GROUP_SECURITY_INFORMATION;
        }

        using var handle = OpenNoFollow(path, access, "Modification des permissions de");
        VerifyIdentity(handle, expectedIdentity, path);
        fixed (byte* sd = securityDescriptor)
        {
            if (!SetKernelObjectSecurity(handle, info, sd))
            {
                throw Error(Marshal.GetLastPInvokeError(), "Modification des permissions de", path);
            }
        }
    }

    public bool TrySetShortName(string path, FileIdentity expectedIdentity, string shortName)
    {
        var handle = CreateFile(Ext(path), GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        using (handle)
        {
            if (handle.IsInvalid)
            {
                return false;
            }

            VerifyIdentity(handle, expectedIdentity, path);
            return SetFileShortName(handle, shortName);
        }
    }

    public void Delete(string path, FileIdentity expectedIdentity)
    {
        using var handle = OpenNoFollow(path, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE, "Suppression de");
        VerifyIdentity(handle, expectedIdentity, path);
        MarkForDeletion(handle, path);
    }

    private static void MarkForDeletion(SafeFileHandle handle, string path)
    {
        var flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, &flags, sizeof(uint)))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is not (ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION))
        {
            throw Error(error, "Suppression de", path);
        }

        // Repli (anciens Windows) : retirer la lecture seule puis marquer pour suppression.
        var info = new FILE_BASIC_INFO { FileAttributes = FILE_ATTRIBUTE_NORMAL };
        SetFileInformationByHandle(handle, FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO));
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, &delete, 1))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Suppression de", path);
        }
    }

    public void FlushFile(string path, FileIdentity expectedIdentity)
    {
        using var handle = OpenNoFollow(path, GENERIC_WRITE | FILE_READ_ATTRIBUTES, "Vidage sur disque de");
        VerifyIdentity(handle, expectedIdentity, path);
        if (!FlushFileBuffers(handle))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Vidage sur disque de", path);
        }
    }

    public bool TryFlushVolume(string path)
    {
        var volume = GetVolumeInfo(path);
        if (volume.RootPath.Length != 3)
        {
            return false;
        }

        using var handle = CreateFile(@"\\.\" + volume.RootPath[..2], GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        return !handle.IsInvalid && FlushFileBuffers(handle);
    }

    public void AppendToFile(string path, ReadOnlySpan<byte> data)
    {
        using var handle = Open(path, GENERIC_WRITE | GENERIC_READ, FILE_SHARE_READ, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, "Écriture de");
        var length = RandomAccess.GetLength(handle);
        RandomAccess.Write(handle, data, length);
        if (!FlushFileBuffers(handle))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Vidage sur disque de", path);
        }
    }

    public void WriteNewFile(string path, ReadOnlySpan<byte> data)
    {
        using var handle = Open(path, GENERIC_WRITE, 0, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, "Création de");
        RandomAccess.Write(handle, data, 0);
        if (!FlushFileBuffers(handle))
        {
            throw Error(Marshal.GetLastPInvokeError(), "Vidage sur disque de", path);
        }
    }
}
