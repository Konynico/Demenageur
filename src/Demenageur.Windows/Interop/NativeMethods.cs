using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Demenageur.Windows.Interop;

/// <summary>Déclarations Win32 utilisées par Déménageur. Aucune fonction d'écriture du registre n'est déclarée.</summary>
internal static unsafe partial class NativeMethods
{
    // ─── Accès ───
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_ALL = 0x10000000;
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;
    public const uint FILE_APPEND_DATA = 0x0004;
    public const uint FILE_READ_EA = 0x0008;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint DELETE = 0x00010000;
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint WRITE_OWNER = 0x00080000;
    public const uint SYNCHRONIZE = 0x00100000;

    // ─── Partage / création ───
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint FILE_SHARE_DELETE = 4;
    public const uint FILE_SHARE_ALL = 7;
    public const uint CREATE_NEW = 1;
    public const uint OPEN_EXISTING = 3;
    public const uint OPEN_ALWAYS = 4;

    // ─── Drapeaux ───
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // ─── Erreurs ───
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_DRIVE = 15;
    public const int ERROR_NOT_SAME_DEVICE = 17;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_CRC = 23;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_INVALID_NAME = 123;
    public const int ERROR_DIR_NOT_EMPTY = 145;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_DIRECTORY = 267;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_CANCELLED = 1223;
    public const int ERROR_REQUEST_ABORTED = 1235;
    public const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const int ERROR_NOT_A_REPARSE_POINT = 4390;
    public const int ERROR_DELETE_PENDING = 303;

    // ─── Classes d'information (GetFileInformationByHandleEx / SetFileInformationByHandle) ───
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileRenameInfo = 3;
    public const int FileDispositionInfo = 4;
    public const int FileStreamInfo = 7;
    public const int FileAttributeTagInfo = 9;
    public const int FileIdBothDirectoryInfo = 10;
    public const int FileIdBothDirectoryRestartInfo = 11;
    public const int FileIdInfo = 18;
    public const int FileDispositionInfoEx = 21;
    public const int FileCaseSensitiveInfo = 23;

    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;
    public const uint FILE_CS_FLAG_CASE_SENSITIVE_DIR = 0x1;

    // ─── FSCTL ───
    public const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    public const int MAXIMUM_REPARSE_DATA_BUFFER_SIZE = 16 * 1024;

    // ─── Sécurité ───
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint GROUP_SECURITY_INFORMATION = 0x2;
    public const uint DACL_SECURITY_INFORMATION = 0x4;

    // ─── CopyFile2 ───
    public const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    public const uint COPY_FILE_NO_BUFFERING = 0x00001000;
    public const uint COPY_FILE_DONT_REQUEST_DEST_WRITE_DAC = 0x02000000;
    public const int COPYFILE2_CALLBACK_CHUNK_FINISHED = 2;
    public const uint COPYFILE2_PROGRESS_CONTINUE = 0;
    public const uint COPYFILE2_PROGRESS_CANCEL = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYFILE2_EXTENDED_PARAMETERS
    {
        public uint dwSize;
        public uint dwCopyFlags;
        public int* pfCancel;
        public delegate* unmanaged[Stdcall]<byte*, nint, uint> pProgressRoutine;
        public nint pvCallbackContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public void* lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES_SINGLE
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    public const uint SE_PRIVILEGE_ENABLED = 0x2;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    public const uint TOKEN_QUERY = 0x8;

    // ─── kernel32 ───

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string path, SECURITY_ATTRIBUTES* securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFileWithSecurity(string fileName, uint desiredAccess, uint shareMode, SECURITY_ATTRIBUTES* securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CopyFile2", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CopyFile2(string existingFileName, string newFileName, COPYFILE2_EXTENDED_PARAMETERS* extendedParameters);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathSize, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileShortNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileShortName(SafeFileHandle file, string shortName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    // ─── advapi32 ───

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetKernelObjectSecurity(SafeFileHandle handle, uint requestedInformation, byte* securityDescriptor, uint length, out uint lengthNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte* securityDescriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandleWrapper tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(SafeAccessTokenHandleWrapper tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, TOKEN_PRIVILEGES_SINGLE* newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}

/// <summary>Handle de jeton de processus.</summary>
internal sealed class SafeAccessTokenHandleWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeAccessTokenHandleWrapper()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
