using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Cloud;

/// <summary>
/// The Win32 and Cloud Files calls behind <see cref="CloudFiles"/>, with the constants and layouts
/// taken from the Windows SDK's <c>cfapi.h</c> (10.0.26100.0).
/// </summary>
internal static unsafe partial class CloudFilesNative
{
    public const int Success = 0;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE)</c>: the handle is open on an ordinary file.</summary>
    public const int NotACloudFile = unchecked((int)0x8007_0178);

    public const uint FileAttributeDirectory = 0x0010;
    public const uint FileAttributeReparsePoint = 0x0400;

    /// <summary><c>CF_PLACEHOLDER_STATE_PLACEHOLDER</c>.</summary>
    public const uint PlaceholderStatePlaceholder = 0x0001;

    /// <summary><c>CF_PLACEHOLDER_STATE_INVALID</c>.</summary>
    public const uint PlaceholderStateInvalid = 0xFFFF_FFFF;

    /// <summary><c>CF_PIN_STATE_UNPINNED</c>, set with <c>CF_SET_PIN_FLAG_NONE</c>: this one file, never a tree.</summary>
    public const int PinStateUnpinned = 2;
    public const int SetPinFlagNone = 0;

    public const int PlaceholderInfoStandard = 1;
    public const int SyncRootInfoProvider = 2;

    /// <summary><c>CF_PROVIDER_STATUS_DISCONNECTED</c>, <c>_TERMINATED</c> and <c>_ERROR</c>.</summary>
    public const uint ProviderDisconnected = 0x0000_0000;
    public const uint ProviderTerminated = 0xC000_0001;
    public const uint ProviderError = 0xC000_0002;

    /// <summary>
    /// <c>CF_PLACEHOLDER_MAX_FILE_IDENTITY_LENGTH</c> beyond the fixed part of the standard record, so a
    /// sync app's longest identity still fits and the call never answers <c>ERROR_MORE_DATA</c>.
    /// </summary>
    public const int PlaceholderInfoBufferSize = 4096 + 128;

    /// <summary>Two <c>WCHAR[256]</c> names after the status.</summary>
    public const int ProviderInfoBufferSize = 4 + (2 * 256 * sizeof(char));

    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const int FileBasicInfoClass = 0;

    /// <summary><c>PHCM_EXPOSE_PLACEHOLDERS</c>.</summary>
    public const sbyte ExposePlaceholders = 2;

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const uint FindFirstExLargeFetch = 0x0002;
    public static readonly nint InvalidHandle = -1;

    /// <summary>
    /// A handle that can read a placeholder's state and change its pin, and nothing else.
    ///
    /// <para><b>Attributes only</b>, which is the access Microsoft documents as enough for both calls,
    /// and which cannot read the file's data, so it cannot start a download. <b>The link itself,
    /// never its target</b>: a file replaced by a link between the preview and the clean is then
    /// read as the ordinary reparse point it is, and nothing outside the sync root is reached.</para>
    /// </summary>
    public static SafeFileHandle OpenForState(string extendedPath) =>
        CreateFile(
            extendedPath,
            FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            templateFile: 0);

    public static bool TryBasicInfo(SafeFileHandle handle, out FileBasicInfo info) =>
        GetFileInformationByHandleEx(handle, FileBasicInfoClass, out info, sizeof(FileBasicInfo));

    [StructLayout(LayoutKind.Sequential)]
    public struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    /// <summary>The fixed part of <c>CF_PLACEHOLDER_STANDARD_INFO</c>; the file identity follows it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PlaceholderStandardInfo
    {
        public long OnDiskDataSize;
        public long ValidatedDataSize;
        public long ModifiedDataSize;
        public long PropertiesSize;
        public int PinState;
        public int InSyncState;
        public long FileId;
        public long SyncRootFileId;
        public uint FileIdentityLength;
    }

    /// <summary>
    /// <c>WIN32_FIND_DATAW</c>. Packed to four because its times are <c>FILETIME</c>s, pairs of
    /// <c>DWORD</c>s aligned to four, and a <see cref="long"/> aligned to eight would move every field
    /// after the attributes, the name included.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;

        /// <summary>The reparse tag, where <see cref="FileAttributes"/> carries the reparse point attribute.</summary>
        public uint Reserved0;
        public uint Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileBasicInfo information,
        int bufferSize);

    /// <summary>
    /// A handle that follows every link, for learning where a folder really is. Attributes only, like
    /// <see cref="OpenForState"/>.
    /// </summary>
    public static SafeFileHandle OpenToResolve(string extendedPath) =>
        CreateFile(extendedPath, FileReadAttributes, ShareAll, securityAttributes: 0, OpenExisting, BackupSemantics, templateFile: 0);

    /// <summary>
    /// The normalised path of whatever <paramref name="handle"/> is open on, with its drive letter, or
    /// null where Windows would not say.
    /// </summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[LongestPath];

        fixed (char* chars = buffer)
        {
            var length = GetFinalPathNameByHandle(handle, chars, LongestPath, FileNameNormalized);

            return length is > 0 and < LongestPath ? new string(chars, 0, (int)length) : null;
        }
    }

    /// <summary>The longest path Windows accepts in extended-length form.</summary>
    private const int LongestPath = 32_768;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>.</summary>
    private const uint FileNameNormalized = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint FindFirstFileEx(
        string fileName,
        int infoLevel,
        out FindData findData,
        int searchOp,
        nint searchFilter,
        uint additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint findFile, out FindData findData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint findFile);

    /// <summary>
    /// Set how placeholders appear to the calling thread, and answer the mode it had, or a negative
    /// value where it could not be set.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    public static partial sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);

    [LibraryImport("cldapi.dll")]
    public static partial uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);

    [LibraryImport("cldapi.dll")]
    public static partial int CfGetPlaceholderInfo(
        SafeFileHandle fileHandle,
        int infoClass,
        void* infoBuffer,
        uint infoBufferLength,
        out uint returnedLength);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfGetSyncRootInfoByPath(
        string filePath,
        int infoClass,
        void* infoBuffer,
        uint infoBufferLength,
        out uint returnedLength);

    [LibraryImport("cldapi.dll")]
    public static partial int CfSetPinState(SafeFileHandle fileHandle, int pinState, int pinFlags, nint overlapped);
}
