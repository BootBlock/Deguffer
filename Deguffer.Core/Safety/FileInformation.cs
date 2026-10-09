using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>What a handle opened on a path is for, which decides whether a link on it is followed.</summary>
internal enum HandleUse
{
    /// <summary>Learning where a path really is, so every link on the way, its own name included, is followed.</summary>
    Resolve,

    /// <summary>Describing the entry the path names: a link is described as the link, never its target.</summary>
    Describe,
}

/// <summary>Opens a handle for attributes alone on a path in the form it reaches Windows.</summary>
internal delegate SafeFileHandle HandleOpener(string extendedPath, HandleUse use);

/// <summary>Reads a file's identity through a handle by one route, or answers false where Windows would not say.</summary>
internal delegate bool IdentityReader(SafeFileHandle handle, IdentityRoute route, out FileIdentity identity);

/// <summary>
/// Lists every name of the file at a path in the form it reaches Windows, each from the top of the
/// file's volume, or answers null where Windows would not list them.
/// </summary>
internal delegate IReadOnlyList<string>? NameLister(string extendedPath);

/// <summary>
/// What Windows says about a file or folder through a handle opened for its attributes alone, which
/// reads no data and so can neither download a cloud file nor be refused for want of read access.
/// Stateless apart from how it opens a handle and reads an identity, so one instance serves every
/// caller (G5).
///
/// <para><b>The one declaration of each call that describes a file through a handle</b>:
/// <c>GetFileInformationByHandleEx</c> for every class Deguffer reads, the older
/// <c>GetFileInformationByHandle</c>, <c>GetFinalPathNameByHandle</c>, and <c>FindFirstFileNameW</c>,
/// which lists a file's names, each from the top of its volume as
/// <see cref="VolumeCalls.MountPointOf"/> gives it. The cloud files, the occupancy probe and the
/// hard-link scanner open and read through the members here rather than declaring their own.</para>
/// </summary>
internal sealed unsafe partial class FileInformation
{
    public static FileInformation Default { get; } = new(Open, ReadIdentity);

    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;

    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;

    private const int ErrorHandleEof = 38;
    private static readonly nint InvalidFind = -1;

    /// <summary>The longest path Windows accepts in extended-length form.</summary>
    private const int LongestPath = 32_768;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>.</summary>
    private const uint FileNameNormalized = 0;

    private readonly HandleOpener _open;
    private readonly IdentityReader _identify;
    private readonly NameLister _listNames;

    /// <param name="open">
    /// Opens the handle a path is resolved or described through, given the path in the form it
    /// reaches Windows. A test wraps <see cref="Open"/> to see that form (§6.3): the call is a raw
    /// <c>CreateFileW</c>, and on a machine that allows long paths a deep folder opens whether or not
    /// the path carries the extended prefix, so only the form itself can show it was given. A test
    /// also has Windows refuse a path here, by answering an invalid handle with the error it chooses.
    /// </param>
    /// <param name="identify">
    /// Reads an identity by one route, so a test can stand for a file system that answers only the
    /// older call, or neither.
    /// </param>
    /// <param name="listNames">
    /// Lists a file's names, so a test can see the form of the path that reaches
    /// <c>FindFirstFileNameW</c> (§6.3), which takes a path rather than a handle. Where none is
    /// given, <see cref="ListNames"/>.
    /// </param>
    internal FileInformation(HandleOpener open, IdentityReader identify, NameLister? listNames = null)
    {
        _open = open;
        _identify = identify;
        _listNames = listNames ?? ListNames;
    }

    /// <summary>
    /// Where <paramref name="path"/> really is once every link on the way to it, its own name
    /// included, is followed, in the extended form Windows answers with, or null where Windows would
    /// not open it. A substituted drive letter is followed too, because it is a link the object
    /// manager keeps.
    ///
    /// <para>Null says only that no answer was had: the path may be absent, or Windows may have
    /// refused to open it. A caller never reads it as absence.</para>
    /// </summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public string? FinalPath(string path)
    {
        using var handle = _open(LongPath.Extended(path), HandleUse.Resolve);

        return handle.IsInvalid ? null : FinalPathOf(handle);
    }

    /// <summary>
    /// The route that identifies the files on the volume holding <paramref name="folder"/>, or null
    /// where neither call identifies the folder, which no search of that volume can do without.
    /// <c>FileIdInfo</c> is asked first, and the older call only where it gives no answer.
    ///
    /// <para>Asked through a handle that follows links, because the folder a volume is mounted at is
    /// a link on the volume holding it: opened as the link, it would answer for that volume rather
    /// than the one whose files are searched.</para>
    /// </summary>
    public IdentityRoute? IdentityRouteOf(string folder)
    {
        using var handle = _open(LongPath.Extended(folder), HandleUse.Resolve);

        if (handle.IsInvalid)
        {
            return null;
        }

        if (_identify(handle, IdentityRoute.FileId, out _))
        {
            return IdentityRoute.FileId;
        }

        return _identify(handle, IdentityRoute.Legacy, out _) ? IdentityRoute.Legacy : null;
    }

    /// <summary>
    /// The entry at <paramref name="path"/> as it is now: its identity by <paramref name="route"/>,
    /// its length, how many names it has, its last-modified time, its attributes and its reparse
    /// tag, all through one handle that follows no link and recalls nothing.
    ///
    /// <para>Gone only where Windows said the file or a folder on the way is not there. Every other
    /// failure, to open or to read, is <see cref="FileReadingResult.Unreadable"/>.</para>
    /// </summary>
    /// <param name="route">The route the volume's files are identified by, never mixed within one volume.</param>
    public FileReading Describe(string path, IdentityRoute route)
    {
        using var handle = _open(LongPath.Extended(path), HandleUse.Describe);

        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() is FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound
                ? FileReading.Gone
                : FileReading.Unreadable;
        }

        return Describe(handle, route);
    }

    /// <summary>
    /// The file <paramref name="handle"/> is open on, described as <see cref="Describe(string, IdentityRoute)"/>
    /// describes a path, or <see cref="FileReadingResult.Unreadable"/> where Windows would not say.
    /// Never <see cref="FileReadingResult.Gone"/>: a file held open is there.
    ///
    /// <para>For a handle already held, such as one opened to read a file's content, so the file
    /// read can be checked to be the file that was described by its path, through the handle the
    /// bytes came from rather than a path that can name another file by then.</para>
    /// </summary>
    public FileReading Describe(SafeFileHandle handle, IdentityRoute route)
    {
        if (!_identify(handle, route, out var identity)
            || !TryStandard(handle, out var standard)
            || !TryBasic(handle, out var basic)
            || !TryAttributeTag(handle, out var tag)
            || FinalPathOf(handle) is not { } final)
        {
            return FileReading.Unreadable;
        }

        return new FileReading(FileReadingResult.Identified, new FileDescription(
            identity,
            LongPath.Display(final),
            standard.EndOfFile,
            (int)standard.NumberOfLinks,
            DateTime.FromFileTimeUtc(basic.LastWriteTime),
            (FileAttributes)basic.FileAttributes,
            tag.ReparseTag));
    }

    /// <summary>
    /// Every name of the file at <paramref name="path"/>, in display form, or null where Windows
    /// would not list them. Windows gives each name from the top of the file's volume, which is
    /// asked of the same path.
    /// </summary>
    public IReadOnlyList<string>? NamesOf(string path)
    {
        if (VolumeCalls.MountPointOf(path) is not { } top || _listNames(LongPath.Extended(path)) is not { } names)
        {
            return null;
        }

        return [.. names.Select(name => Path.Join(top, name.TrimStart('\\')))];
    }

    /// <summary>Every name of the file at <paramref name="extendedPath"/>, as Windows lists them.</summary>
    internal static IReadOnlyList<string>? ListNames(string extendedPath)
    {
        var buffer = new char[LongestPath];

        fixed (char* chars = buffer)
        {
            var length = (uint)LongestPath;
            var find = FindFirstFileName(extendedPath, 0, ref length, chars);

            if (find == InvalidFind)
            {
                return null;
            }

            try
            {
                List<string> names = [];

                do
                {
                    names.Add(new string(chars));
                    length = LongestPath;
                }
                while (FindNextFileName(find, ref length, chars));

                // The listing ends by reaching its end; anything else left names unlisted.
                return Marshal.GetLastPInvokeError() == ErrorHandleEof ? names : null;
            }
            finally
            {
                FindClose(find);
            }
        }
    }

    /// <summary>
    /// The normalised path of whatever <paramref name="handle"/> is open on, with its drive letter, or
    /// null where Windows would not say.
    /// </summary>
    public static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[LongestPath];

        fixed (char* chars = buffer)
        {
            var length = GetFinalPathNameByHandle(handle, chars, LongestPath, FileNameNormalized);

            return length is > 0 and < LongestPath ? new string(chars, 0, (int)length) : null;
        }
    }

    /// <summary>
    /// A handle for attributes alone, which every other process may share. Opened to resolve, it
    /// follows every link; opened to describe, it is open on the link itself, so a file replaced by a
    /// link is read as the link it now is and nothing it points at is reached, and a cloud file is
    /// never recalled. Microsoft documents this access as enough to read a placeholder's state and
    /// change its pin.
    /// </summary>
    /// <param name="extendedPath">The path in the extended form §6.3 requires.</param>
    internal static SafeFileHandle Open(string extendedPath, HandleUse use) =>
        CreateFile(
            extendedPath,
            FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            use is HandleUse.Describe ? BackupSemantics | OpenReparsePoint : BackupSemantics,
            templateFile: 0);

    /// <summary>The identity by <paramref name="route"/>, as Windows reads it through the handle.</summary>
    internal static bool ReadIdentity(SafeFileHandle handle, IdentityRoute route, out FileIdentity identity)
    {
        if (route is IdentityRoute.FileId)
        {
            var read = GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo id, sizeof(FileIdInfo));
            identity = new FileIdentity(id.VolumeSerialNumber, new UInt128(id.FileIdHigh, id.FileIdLow));
            return read;
        }

        var legacy = GetFileInformationByHandle(handle, out var info);
        identity = new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        return legacy;
    }

    public static bool TryStandard(SafeFileHandle handle, out FileStandardInfo info) =>
        GetFileInformationByHandleEx(handle, FileStandardInfoClass, out info, sizeof(FileStandardInfo));

    public static bool TryBasic(SafeFileHandle handle, out FileBasicInfo info) =>
        GetFileInformationByHandleEx(handle, FileBasicInfoClass, out info, sizeof(FileBasicInfo));

    /// <summary>
    /// The attributes and reparse tag of what <paramref name="handle"/> is open on, which is the link
    /// itself where it was opened to describe. The tag is zero for an entry that carries none.
    /// </summary>
    public static bool TryAttributeTag(SafeFileHandle handle, out FileAttributeTagInfo info) =>
        GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out info, sizeof(FileAttributeTagInfo));

    [StructLayout(LayoutKind.Sequential)]
    public struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary><c>FILE_ID_INFO</c>: the serial number, then <c>FILE_ID_128</c>'s sixteen bytes, low half first.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    /// <summary>
    /// <c>BY_HANDLE_FILE_INFORMATION</c>. Packed to four because its times are <c>FILETIME</c>s, pairs
    /// of <c>DWORD</c>s aligned to four, and a <see cref="long"/> aligned to eight would move every
    /// field after the attributes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, int bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStandardInfo information, int bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileBasicInfo information, int bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, int bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstFileName(string fileName, uint flags, ref uint length, char* linkName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextFileName(nint find, ref uint length, char* linkName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);
}
