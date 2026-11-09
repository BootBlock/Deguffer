using System.Buffers;
using System.Runtime.InteropServices;
using Deguffer.Core.Scanning;
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

/// <summary>What a file opened by <see cref="FileInformation.OpenHeld"/> is held for, which decides what every other program may do to it meanwhile.</summary>
internal enum HeldFor
{
    /// <summary>Kept: every other program may read it, and none may write to it, rename it or delete it.</summary>
    Keeping,

    /// <summary>
    /// Removed through this handle: every other program may read it, rename it or delete it, as the
    /// shell does when it moves it into the Recycle Bin, and none may write to it.
    /// </summary>
    Removing,
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

/// <summary>The normalised path of whatever a handle is open on, as <see cref="FileInformation.FinalPathOf"/> reads it, or null where Windows would not say.</summary>
internal delegate string? FinalPathReader(SafeFileHandle handle);

/// <summary>
/// What Windows says about a file or folder through a handle opened for its attributes alone, which
/// reads no data and so can neither download a cloud file nor be refused for want of read access.
/// Stateless apart from how it opens a handle and reads an identity, so one instance serves every
/// caller (G5).
///
/// <para><b>The one declaration of each call that describes a file through a handle</b>:
/// <c>GetFileInformationByHandleEx</c> for every class Deguffer reads, the older
/// <c>GetFileInformationByHandle</c>, <c>GetFinalPathNameByHandle</c>, <c>FindFirstFileNameW</c>,
/// which lists a file's names, each from the top of its volume as
/// <see cref="MountPointLookup.MountPointOf"/> gives it, and <c>OpenFileById</c>, which opens a file
/// described here by its number rather than its path. The cloud files, the occupancy probe and the
/// hard-link scanner open and read through the members here rather than declaring their own.</para>
///
/// <para><see cref="Standard"/> is the one read here that may need no handle: it asks
/// <see cref="StatByName"/> first and turns to a handle only where that does not answer.</para>
/// </summary>
internal sealed unsafe partial class FileInformation
{
    public static FileInformation Default { get; } = new(Open, ReadIdentity);

    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint ShareRead = 0x0001;
    private const uint SequentialScan = 0x0800_0000;
    private const uint Delete = 0x0001_0000;
    private const uint ShareDelete = 0x0004;

    /// <summary><c>FILE_ID_TYPE</c>'s <c>FileIdType</c>: the 64-bit number the older call gives.</summary>
    private const int FileIdType = 0;

    /// <summary><c>FILE_ID_TYPE</c>'s <c>ExtendedFileIdType</c>: <c>FILE_ID_128</c>, as <c>FileIdInfo</c> gives it.</summary>
    private const int ExtendedFileIdType = 2;

    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const int FileStreamInfoClass = 7;

    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorMoreData = 234;

    /// <summary><c>FILE_NAMED_STREAMS</c>: the volume can hold streams beside a file's content.</summary>
    private const uint NamedStreamsSupported = 0x0004_0000;

    /// <summary>The most a listing of one file's streams may take before Windows' answer is given up on.</summary>
    private const int LongestStreamListing = 16 * 1024 * 1024;

    /// <summary><c>FILE_STREAM_INFO</c>'s fixed part: the next entry's offset, the name's length, the stream's size and allocation.</summary>
    private const int StreamEntryHeader = 24;

    /// <summary>How Windows names a file's main, unnamed stream in a listing of its streams.</summary>
    private const string MainStream = "::$DATA";
    private static readonly nint InvalidFind = -1;

    /// <summary>The longest path Windows accepts in extended-length form.</summary>
    private const int LongestPath = 32_768;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>.</summary>
    private const uint FileNameNormalized = 0;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_NT</c>.</summary>
    private const uint FileNameNormalizedOnDevice = 0x2;

    private readonly HandleOpener _open;
    private readonly IdentityReader _identify;
    private readonly NameLister _listNames;
    private readonly StandardByNameReader _readByName;
    private readonly FinalPathReader _finalPath;
    private readonly Func<string, string?> _mountPointOf;

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
    /// <param name="readByName">
    /// Reads a file's link count and sizes with no handle, so a test can stand for a file system that
    /// does not answer by name and see the read turn to <paramref name="open"/>. Where none is given,
    /// <see cref="StatByName.Read"/>.
    /// </param>
    /// <param name="finalPath">
    /// Reads where a handle is open on, so a test can see which reads ask Windows for a path, and have
    /// Windows refuse to give one. Where none is given, <see cref="FinalPathOf"/>.
    /// </param>
    /// <param name="mountPointOf">
    /// Says where a path's volume is mounted, so a test can count the questions and have Windows
    /// decline to answer. Where none is given, <see cref="MountPointLookup.MountPointOf"/>.
    /// </param>
    internal FileInformation(
        HandleOpener open,
        IdentityReader identify,
        NameLister? listNames = null,
        StandardByNameReader? readByName = null,
        FinalPathReader? finalPath = null,
        Func<string, string?>? mountPointOf = null)
    {
        _open = open;
        _identify = identify;
        _listNames = listNames ?? ListNames;
        _readByName = readByName ?? StatByName.Read;
        _finalPath = finalPath ?? FinalPathOf;
        _mountPointOf = mountPointOf ?? MountPointLookup.Machine.MountPointOf;
    }

    /// <summary>
    /// The link count and both sizes of the file at <paramref name="extendedPath"/>, read by its name
    /// while <paramref name="route"/> allows, and otherwise through a handle opened to describe it, or
    /// null where neither answers. A file system that does not answer by name declines
    /// <paramref name="route"/>, so the rest of its walk opens a handle without asking first.
    ///
    /// <para>Both read the entry the name is now, never what a link there points at, so a caller
    /// gets the same answer whichever route gave it.</para>
    /// </summary>
    /// <param name="extendedPath">The path in the extended form §6.3 requires.</param>
    public FileStandardInfo? Standard(string extendedPath, ByNameRoute route) =>
        !route.Declined && TryByName(0, LongPath.ObjectManagerForm(extendedPath), route, out var answer)
            ? answer
            : ThroughHandle(extendedPath);

    /// <summary>
    /// <see cref="Standard(string, ByNameRoute)"/> for a file a walk listed, read by its bare name
    /// relative to <paramref name="directory"/>, the handle the walk holds open on the directory it
    /// listed, so neither the file's full path nor its name for the object manager is built. The full
    /// path is built only where a handle must answer, or where <paramref name="directory"/> is zero,
    /// as it is for a listing that opened nothing.
    /// </summary>
    /// <param name="directory">
    /// <see cref="DirectoryContents.Handle"/> of the listing <paramref name="file"/> came from.
    /// </param>
    public FileStandardInfo? Standard(nint directory, in WalkEntry file, ByNameRoute route)
    {
        if (directory == 0)
        {
            return Standard(file.FullName, route);
        }

        return !route.Declined && TryByName(directory, file.NameSpan, route, out var answer)
            ? answer
            : ThroughHandle(file.FullName);
    }

    /// <summary>
    /// Whether one read by name settled the file: read, or gone, which is <paramref name="answer"/>
    /// null. A file system that does not answer the class declines <paramref name="route"/>, and that
    /// or any other refusal leaves the file to a handle.
    /// </summary>
    private bool TryByName(nint directory, ReadOnlySpan<char> name, ByNameRoute route, out FileStandardInfo? answer)
    {
        var reading = _readByName(directory, name, out var read);

        if (reading is ByNameReading.Unsupported)
        {
            route.Decline();
        }

        answer = reading is ByNameReading.Read ? read : null;

        return reading is ByNameReading.Read or ByNameReading.Gone;
    }

    /// <summary>The link count and sizes read through a handle opened to describe the file, or null.</summary>
    private FileStandardInfo? ThroughHandle(string extendedPath)
    {
        using var handle = _open(extendedPath, HandleUse.Describe);

        return !handle.IsInvalid && TryStandard(handle, out var info) ? info : null;
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

        return handle.IsInvalid ? null : _finalPath(handle);
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
    /// failure, to open or to read, where Windows would not say where the handle is open included,
    /// is <see cref="FileReadingResult.Unreadable"/>.</para>
    /// </summary>
    /// <param name="route">The route the volume's files are identified by, never mixed within one volume.</param>
    public FileReading Describe(string path, IdentityRoute route)
    {
        using var held = Hold(path, route);

        return Locate(held);
    }

    /// <summary>
    /// The state of the entry at <paramref name="path"/>, read as <see cref="Describe(string, IdentityRoute)"/>
    /// reads it but without its path, with the attributes-only handle it was read through kept open
    /// where it was identified, for a caller that goes on to open the file by its number on that
    /// volume (<see cref="OpenById"/>). The caller disposes it, and asks <see cref="Locate"/> where it
    /// needs the path.
    /// </summary>
    public HeldFile Hold(string path, IdentityRoute route)
    {
        var handle = _open(LongPath.Extended(path), HandleUse.Describe);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();

            return new HeldFile(
                error is FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound ? FileStateReading.Gone : FileStateReading.Unreadable,
                Handle: null);
        }

        var reading = State(handle, route);

        if (reading.Result is not FileReadingResult.Identified)
        {
            handle.Dispose();
            return new HeldFile(reading, Handle: null);
        }

        return new HeldFile(reading, handle);
    }

    /// <summary>
    /// What <paramref name="held"/> read, with where its handle says the file is, or
    /// <see cref="FileReadingResult.Unreadable"/> where Windows would not say. The caller still
    /// disposes <paramref name="held"/>.
    /// </summary>
    public FileReading Locate(HeldFile held) =>
        held.Handle is { } handle ? Located(held.Reading, handle) : new FileReading(held.Reading.Result, Description: null);

    /// <summary>
    /// The file <paramref name="handle"/> is open on, described as <see cref="Describe(string, IdentityRoute)"/>
    /// describes a path, or <see cref="FileReadingResult.Unreadable"/> where Windows would not say.
    /// Never <see cref="FileReadingResult.Gone"/>: a file held open is there.
    ///
    /// <para>For a handle already held, where the caller needs where the file is as well as what it
    /// is. A caller that compares only what it is asks <see cref="State"/>.</para>
    /// </summary>
    public FileReading Describe(SafeFileHandle handle, IdentityRoute route) => Located(State(handle, route), handle);

    /// <summary>
    /// The file <paramref name="handle"/> is open on, read as <see cref="Describe(SafeFileHandle, IdentityRoute)"/>
    /// reads it but without its path, or <see cref="FileReadingResult.Unreadable"/> where Windows
    /// would not say. Never <see cref="FileReadingResult.Gone"/>: a file held open is there.
    ///
    /// <para>For a handle already held, such as one opened to read a file's content, so the file
    /// read can be checked to be the file that was described by its path, through the handle the
    /// bytes came from rather than a path that can name another file by then.</para>
    /// </summary>
    public FileStateReading State(SafeFileHandle handle, IdentityRoute route)
    {
        if (!_identify(handle, route, out var identity)
            || !TryStandard(handle, out var standard)
            || !TryBasic(handle, out var basic)
            || !TryAttributeTag(handle, out var tag))
        {
            return FileStateReading.Unreadable;
        }

        return new FileStateReading(FileReadingResult.Identified, new FileState(
            identity,
            standard.EndOfFile,
            standard.AllocationSize,
            (int)standard.NumberOfLinks,
            DateTime.FromFileTimeUtc(basic.LastWriteTime),
            DateTime.FromFileTimeUtc(basic.ChangeTime),
            (FileAttributes)basic.FileAttributes,
            tag.ReparseTag));
    }

    /// <summary><paramref name="reading"/> with where <paramref name="handle"/> is open, or unreadable where Windows would not say.</summary>
    private FileReading Located(FileStateReading reading, SafeFileHandle handle)
    {
        if (reading.State is not { } state)
        {
            return new FileReading(reading.Result, Description: null);
        }

        return _finalPath(handle) is { } final
            ? new FileReading(FileReadingResult.Identified, new FileDescription(state, LongPath.Display(final)))
            : FileReading.Unreadable;
    }

    /// <summary>
    /// The file numbered <paramref name="identity"/> on the volume <paramref name="volume"/> is open
    /// on, described through a handle opened by that number for attributes alone, so no path is
    /// walked and nothing is recalled.
    ///
    /// <para>Gone only where Windows answered that no file has that number, which an open by number
    /// says as <c>ERROR_INVALID_PARAMETER</c>, measured on NTFS. A file system that does not open by
    /// number may say the same, so a caller reads it as gone only on a volume that opened the file by
    /// its number before. Every other failure is <see cref="FileReadingResult.Unreadable"/>.</para>
    /// </summary>
    /// <param name="volumeRoot">
    /// A path the file's volume is mounted at, opened to name the volume, following the link a
    /// folder it is mounted at is.
    /// </param>
    public FileReading DescribeById(string volumeRoot, FileIdentity identity, IdentityRoute route)
    {
        using var volume = _open(LongPath.Extended(volumeRoot), HandleUse.Resolve);

        if (volume.IsInvalid)
        {
            return FileReading.Unreadable;
        }

        using var handle = OpenFileById(volume, Descriptor(identity, route), FileReadAttributes, ShareAll, securityAttributes: 0, BackupSemantics);

        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? FileReading.Gone : FileReading.Unreadable;
        }

        return Describe(handle, route);
    }

    /// <summary>
    /// Where the volume holding <paramref name="path"/> is mounted, in display form, or null where
    /// Windows will not say: what <see cref="NamesOf"/> gives a file's names from.
    /// </summary>
    public string? MountPointOf(string path) => _mountPointOf(path);

    /// <summary>
    /// Every name of the file at <paramref name="path"/>, in display form, or null where Windows
    /// would not list them. Windows gives each name from the top of the file's volume.
    /// </summary>
    /// <param name="mountPoint">
    /// Where the file's volume is mounted, as <see cref="MountPointOf"/> answers for the file or for
    /// a folder it is found under.
    /// </param>
    public IReadOnlyList<string>? NamesOf(string path, string mountPoint)
    {
        if (_listNames(LongPath.Extended(path)) is not { } names)
        {
            return null;
        }

        return [.. names.Select(name => Path.Join(mountPoint, name.TrimStart('\\')))];
    }

    /// <summary>Every name of the file at <paramref name="extendedPath"/>, as Windows lists them.</summary>
    internal static IReadOnlyList<string>? ListNames(string extendedPath)
    {
        var buffer = ArrayPool<char>.Shared.Rent(LongestPath);

        try
        {
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
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The normalised path of whatever <paramref name="handle"/> is open on, with its drive letter, or
    /// null where Windows would not say.
    /// </summary>
    public static string? FinalPathOf(SafeFileHandle handle) => FinalPathOf(handle, FileNameNormalized);

    /// <summary>
    /// The normalised path of whatever <paramref name="handle"/> is open on, named from its volume's
    /// device, or null where Windows would not say.
    ///
    /// <para>Cheaper than <see cref="FinalPathOf(SafeFileHandle)"/> where many files are compared with
    /// one place: the drive-letter form makes Windows ask the mount manager to name the volume on every
    /// call, and this form does not.</para>
    /// </summary>
    public static VolumeDevicePath? DevicePathOf(SafeFileHandle handle) =>
        FinalPathOf(handle, FileNameNormalizedOnDevice) is { } path ? new VolumeDevicePath(path) : null;

    private static string? FinalPathOf(SafeFileHandle handle, uint flags)
    {
        var buffer = ArrayPool<char>.Shared.Rent(LongestPath);

        try
        {
            fixed (char* chars = buffer)
            {
                var length = GetFinalPathNameByHandle(handle, chars, LongestPath, flags);

                return length is > 0 and < LongestPath ? new string(chars, 0, (int)length) : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
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
        AsNamed(extendedPath, CreateFile(
            extendedPath,
            FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            use is HandleUse.Describe ? BackupSemantics | OpenReparsePoint : BackupSemantics,
            templateFile: 0));

    /// <summary>
    /// The handle an open of <paramref name="extendedPath"/> gave, or an invalid one with
    /// <c>ERROR_DIRECTORY</c> set where the path names a file with a trailing separator, which no file
    /// can be named.
    ///
    /// <para><b>Windows does not give that answer every time.</b> Measured on NTFS on 2026-10-23: an
    /// open of a file just written, through its name with a separator after it, succeeded in about 97%
    /// of tries while eight threads wrote files beside it, in a sync root and outside one, and failed
    /// with <c>ERROR_DIRECTORY</c> once the file had been left alone. An open asking for delete access
    /// failed every time. A caller reads the open as its presence probe, so without this the same path
    /// would be there or not by timing, against <see cref="LongPath.ProbeEntry(string)"/>, which
    /// settles the separator before it asks Windows anything.</para>
    /// </summary>
    /// <param name="opened">What Windows answered, disposed here where this answers otherwise.</param>
    internal static SafeFileHandle AsNamed(string extendedPath, SafeFileHandle opened)
    {
        // A handle that cannot say what it is open on stays as Windows gave it: closing it would read
        // a refusal to describe as absence.
        if (opened.IsInvalid
            || !LongPath.EndsWithSeparator(extendedPath)
            || !TryBasic(opened, out var basic)
            || (basic.FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0)
        {
            return opened;
        }

        opened.Dispose();

        // After the close, which sets the last error of its own.
        Marshal.SetLastPInvokeError(LongPath.ErrorDirectory);

        return new SafeFileHandle(-1, ownsHandle: false);
    }

    /// <summary>
    /// Opens the file numbered <paramref name="identity"/> on the volume <paramref name="volume"/> is
    /// open on, to read its content, sharing only reading, or answers an invalid handle with the
    /// error set where Windows would not open it.
    ///
    /// <para><b>By number, never by a path.</b> Windows walks no path to find it, so a folder or a name
    /// replaced by a link since the file was described cannot send the open through the link to
    /// another file, a share or a cloud file to be recalled. The reparse point of the file itself is
    /// not opened as one, so a deduplicated or compressed file is read through its filter, as its
    /// content, rather than as the stub on disk.</para>
    /// </summary>
    /// <param name="volume">Any handle open on the file's volume: the one it was described through.</param>
    /// <param name="route">The route that read the identity, which decides which kind of number it is.</param>
    public static SafeFileHandle OpenById(SafeFileHandle volume, FileIdentity identity, IdentityRoute route)
    {
        return OpenFileById(volume, Descriptor(identity, route), GenericRead, ShareRead, securityAttributes: 0, SequentialScan);
    }

    /// <summary>
    /// Opens the file at <paramref name="extendedPath"/> to read it and hold it for
    /// <paramref name="use"/>, never sharing writing, or answers an invalid handle with the error set
    /// where Windows would not open it.
    ///
    /// <para><b>By its path, never its number, for two reasons measured on NTFS on 2026-10-09.</b> A
    /// handle opened by number refuses another program's write but not a rename or a delete, however
    /// little it shares, so it cannot hold a copy that must stay; and a disposition set through one is
    /// refused with <c>ERROR_INVALID_PARAMETER</c>, so it cannot delete the copy it compared either.
    /// A handle opened by path does both. The path's own name is opened as itself and never followed,
    /// so a name replaced by a link opens the link, which the caller then finds is not the file it
    /// described.</para>
    /// </summary>
    /// <param name="extendedPath">The path in the extended form §6.3 requires.</param>
    internal static SafeFileHandle OpenHeld(string extendedPath, HeldFor use) =>
        AsNamed(extendedPath, CreateFile(
            extendedPath,
            use is HeldFor.Removing ? GenericRead | Delete : GenericRead,
            use is HeldFor.Removing ? ShareRead | ShareDelete : ShareRead,
            securityAttributes: 0,
            OpenExisting,
            OpenReparsePoint | SequentialScan,
            templateFile: 0));

    /// <summary>
    /// Every named stream of the file <paramref name="handle"/> is open on, as Windows names them
    /// (<c>:name:$DATA</c>) with their lengths, its main stream left out, or null where Windows would
    /// not list them. Asked through a handle already held, so the streams listed are that file's
    /// whatever its path names by then. An attributes-only handle answers it too, measured on NTFS.
    /// </summary>
    public static IReadOnlyList<NamedStream>? StreamsOf(SafeFileHandle handle)
    {
        for (var size = 4096; size <= LongestStreamListing; size *= 2)
        {
            var buffer = new byte[size];

            fixed (byte* bytes = buffer)
            {
                if (GetFileInformationByHandleEx(handle, FileStreamInfoClass, bytes, size))
                {
                    return Streams(bytes, size);
                }

                var error = Marshal.GetLastPInvokeError();

                switch (error)
                {
                    case ErrorHandleEof:
                        // Nothing holds data: a folder.
                        return [];

                    case ErrorMoreData:
                        continue;

                    default:
                        return StreamsWhereRefused(error, VolumeFlagsOf(handle));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The named streams of a file whose file system refused <c>FileStreamInfo</c> with
    /// <paramref name="error"/>, given what its volume says it supports: none, where the volume says it
    /// holds no named streams, which FAT32 and exFAT say and refuse the class with
    /// <c>ERROR_INVALID_PARAMETER</c> (measured on 2026-10-09). Null everywhere else, because a volume
    /// that may hold named streams and would not list them may hold one the copy kept lacks.
    /// </summary>
    /// <param name="volumeFlags">The volume's <c>FILE_*</c> flags, or null where it would not say.</param>
    internal static IReadOnlyList<NamedStream>? StreamsWhereRefused(int error, uint? volumeFlags) =>
        error == ErrorInvalidParameter && volumeFlags is { } flags && (flags & NamedStreamsSupported) == 0 ? [] : null;

    /// <summary>The <c>FILE_*</c> flags of the volume <paramref name="handle"/> is open on, or null where Windows would not say.</summary>
    private static uint? VolumeFlagsOf(SafeFileHandle handle) =>
        GetVolumeInformationByHandle(handle, null, 0, null, null, out var flags, null, 0) ? flags : null;

    /// <summary>The entries of a <c>FILE_STREAM_INFO</c> listing, or null where one runs past the buffer.</summary>
    private static List<NamedStream>? Streams(byte* bytes, int size)
    {
        List<NamedStream> streams = [];
        var offset = 0;

        while (true)
        {
            var entry = bytes + offset;
            var next = *(uint*)entry;
            var nameBytes = *(uint*)(entry + 4);

            if (offset + StreamEntryHeader + nameBytes > size)
            {
                return null;
            }

            var name = new string((char*)(entry + StreamEntryHeader), 0, (int)nameBytes / sizeof(char));

            if (name != MainStream)
            {
                streams.Add(new NamedStream(name, *(long*)(entry + 8)));
            }

            if (next == 0)
            {
                return streams;
            }

            offset += (int)next;
        }
    }

    private static FileIdDescriptor Descriptor(FileIdentity identity, IdentityRoute route) =>
        route is IdentityRoute.FileId
            ? new FileIdDescriptor(ExtendedFileIdType, (ulong)identity.File, (ulong)(identity.File >> 64))
            : new FileIdDescriptor(FileIdType, (ulong)identity.File, 0);

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
    ///
    /// <para><b>Measured on 2026-10-09:</b> FAT32 and exFAT refuse the class with
    /// <c>ERROR_INVALID_PARAMETER</c>, as they hold no reparse points, while they answer
    /// <c>FileBasicInfo</c>. There the attributes are read from that, and an entry they do not mark as
    /// a reparse point carries no tag (<see cref="TagWhereRefused"/>).</para>
    /// </summary>
    public static bool TryAttributeTag(SafeFileHandle handle, out FileAttributeTagInfo info)
    {
        if (GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out info, sizeof(FileAttributeTagInfo)))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();

        if (TryBasic(handle, out var basic) && TagWhereRefused(error, basic.FileAttributes) is { } answered)
        {
            info = answered;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The attributes and tag of an entry whose file system refused <c>FileAttributeTagInfo</c> with
    /// <paramref name="error"/>, given its attributes, or null where that refusal leaves its tag
    /// unknown: any other error, or an entry marked as a reparse point, whose tag says what it is.
    /// </summary>
    internal static FileAttributeTagInfo? TagWhereRefused(int error, uint attributes) =>
        error == ErrorInvalidParameter && (attributes & (uint)System.IO.FileAttributes.ReparsePoint) == 0
            ? new FileAttributeTagInfo { FileAttributes = attributes, ReparseTag = 0 }
            : null;

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
    /// <c>FILE_ID_DESCRIPTOR</c>: its size, the kind of number, then a sixteen-byte union aligned to
    /// eight, which holds the 64-bit number in its first half or <c>FILE_ID_128</c>'s bytes, low half
    /// first, in both.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdDescriptor(int type, ulong low, ulong high)
    {
        public readonly uint Size = (uint)sizeof(FileIdDescriptor);
        public readonly int Type = type;
        public readonly ulong Low = low;
        public readonly ulong High = high;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        in FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint flagsAndAttributes);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, byte* information, int bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationByHandle(
        SafeFileHandle file,
        char* volumeName,
        uint volumeNameLength,
        uint* serialNumber,
        uint* maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemName,
        uint fileSystemNameLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstFileName(string fileName, uint flags, ref uint length, char* linkName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextFileName(nint find, ref uint length, char* linkName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);
}
