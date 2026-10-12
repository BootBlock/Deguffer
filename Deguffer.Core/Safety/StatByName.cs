using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>How a read of a file's link count and sizes by its name ended.</summary>
internal enum ByNameReading
{
    /// <summary>Windows answered.</summary>
    Read,

    /// <summary>
    /// The file's file system does not answer the class by name, so no file on that volume will:
    /// the reader turns to a handle for the rest of its walk.
    /// </summary>
    Unsupported,

    /// <summary>Windows said the file, or a folder on the way to it, is not there.</summary>
    Gone,

    /// <summary>
    /// Any other refusal, which a handle may still answer, so this one file is asked again through
    /// one, as it was asked before the read by name existed.
    /// </summary>
    Refused,
}

/// <summary>Reads a file's link count and sizes by a path in the form it reaches Windows, with no handle.</summary>
internal delegate ByNameReading StandardByNameReader(string extendedPath, out FileInformation.FileStandardInfo info);

/// <summary>
/// A file's link count, allocation size and length from <c>NtQueryInformationByName</c> with
/// <c>FileStatInformation</c>: one call, with no handle opened, closed or seen by a caller, where
/// <see cref="FileInformation.Open"/> and <c>GetFileInformationByHandleEx</c> take three. Every
/// file-system filter sees an open and a close for each handle, so on a large store the opens cost
/// more than the listing.
///
/// <para>The call is present from Windows 10 1709, below the application's minimum, but a file
/// system may not answer the class, which is <see cref="ByNameReading.Unsupported"/>.</para>
///
/// <para><b>It describes the name itself, never what a link there points at</b>, as a handle opened
/// to describe does: Windows answers a symbolic link with the link's own attributes and tag, and a
/// length of nothing. A name replaced by a link between the listing and this read is therefore read
/// as the link it now is, and nothing outside the tree is counted through it.</para>
/// </summary>
internal static unsafe partial class StatByName
{
    /// <summary><c>FILE_INFORMATION_CLASS</c>'s <c>FileStatInformation</c>.</summary>
    private const int FileStatInformationClass = 68;

    /// <summary><c>OBJ_CASE_INSENSITIVE</c>: names are matched as <c>CreateFileW</c> matches them.</summary>
    private const uint CaseInsensitive = 0x40;

    private const int StatusObjectNameNotFound = unchecked((int)0xC000_0034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000_003A);
    private const int StatusNoSuchFile = unchecked((int)0xC000_000F);
    private const int StatusNotImplemented = unchecked((int)0xC000_0002);
    private const int StatusInvalidInfoClass = unchecked((int)0xC000_0003);
    private const int StatusInvalidParameter = unchecked((int)0xC000_000D);
    private const int StatusInvalidDeviceRequest = unchecked((int)0xC000_0010);
    private const int StatusNotSupported = unchecked((int)0xC000_00BB);

    /// <summary>The Win32 extended-length prefix, and the native one it stands for.</summary>
    private const string ExtendedPrefix = @"\\?\";
    private const string NativePrefix = @"\??\";

    /// <summary>The longest name a <c>UNICODE_STRING</c> holds, in characters.</summary>
    private const int LongestName = ushort.MaxValue / sizeof(char);

    /// <param name="extendedPath">The path in the extended form §6.3 requires.</param>
    public static ByNameReading Read(string extendedPath, out FileInformation.FileStandardInfo info)
    {
        info = default;

        // A path that is not in the extended form has no native name to give, and one too long
        // for the native string cannot be named: both are asked through a handle instead, as the
        // read did before this route existed.
        if (NativeName(extendedPath) is not { Length: <= LongestName } name)
        {
            return ByNameReading.Refused;
        }

        FileStatInformation stat;
        int status;

        fixed (char* chars = name)
        {
            var unicode = new UnicodeString
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = chars,
            };

            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                ObjectName = &unicode,
                Attributes = CaseInsensitive,
            };

            IoStatusBlock io;
            status = NtQueryInformationByName(&attributes, &io, &stat, (uint)sizeof(FileStatInformation), FileStatInformationClass);
        }

        var reading = Answer(status);

        if (reading is ByNameReading.Read)
        {
            info = new FileInformation.FileStandardInfo
            {
                AllocationSize = stat.AllocationSize,
                EndOfFile = stat.EndOfFile,
                NumberOfLinks = stat.NumberOfLinks,
                Directory = (stat.FileAttributes & (uint)FileAttributes.Directory) != 0 ? (byte)1 : (byte)0,
            };
        }

        return reading;
    }

    /// <summary>
    /// The native name Windows gives the object manager for <paramref name="extendedPath"/>, or null
    /// where it is not in the extended form. <c>\\?\C:\a</c> is <c>\??\C:\a</c>, and
    /// <c>\\?\UNC\server\share\a</c> is <c>\??\UNC\server\share\a</c>: the prefix is the only part
    /// that differs, which is what the extended form is for.
    /// </summary>
    internal static string? NativeName(string extendedPath) =>
        extendedPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? string.Concat(NativePrefix, extendedPath.AsSpan(ExtendedPrefix.Length))
            : null;

    /// <summary>What an <c>NTSTATUS</c> from the read says, per <see cref="ByNameReading"/>.</summary>
    internal static ByNameReading Answer(int status) => status switch
    {
        >= 0 => ByNameReading.Read,
        StatusObjectNameNotFound or StatusObjectPathNotFound or StatusNoSuchFile => ByNameReading.Gone,

        // What the I/O manager and the file systems answer for a class they do not implement, or a
        // by-name query they do not take.
        StatusInvalidInfoClass or StatusNotImplemented or StatusInvalidParameter
            or StatusInvalidDeviceRequest or StatusNotSupported => ByNameReading.Unsupported,
        _ => ByNameReading.Refused,
    };

    /// <summary><c>FILE_STAT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatInformation
    {
        public long FileId;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long AllocationSize;
        public long EndOfFile;
        public uint FileAttributes;
        public uint ReparseTag;
        public uint NumberOfLinks;
        public uint EffectiveAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationByName(
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        FileStatInformation* fileInformation,
        uint length,
        int fileInformationClass);
}
