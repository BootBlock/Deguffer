using System.Runtime.InteropServices;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Safety;

/// <summary>
/// One directory listed from the disk: opened with <c>NtCreateFile</c>, read with
/// <c>NtQueryDirectoryFile</c> into the worker's own <see cref="ListingMemory"/>, and each entry read
/// where Windows wrote it. The listing every walk takes outside a test.
///
/// <para><b>Why not <see cref="System.IO.Enumeration.FileSystemEnumerator{TResult}"/>.</b> A new one
/// for each directory allocated and freed a native buffer, a finalisable object, a lock and a closure,
/// and the runtime opens the directory with <c>CreateFileW</c>, which cannot ask for
/// <c>FILE_DIRECTORY_FILE</c>. Measured on one machine without administrator rights (#370), reusing the
/// memory and opening the directory as a directory took 11 to 17 percent off a walk on one thread, and
/// 12 to 24 percent off its processor time on sixteen. About half of that was the flag, and why the
/// flag helps is not proven.</para>
///
/// <para><b>§5.3: nothing throws.</b> Every failure is read as the Win32 error its <c>NTSTATUS</c> maps
/// to, which is the error <c>CreateFileW</c> and the runtime's read reported, so
/// <see cref="ListingEnd.Failed"/> gives each the answer it gave before. A file where a directory was
/// expected is <c>STATUS_NOT_A_DIRECTORY</c>, refused.</para>
///
/// <para><b>A directory the walk descended into is opened as itself</b>
/// (<see cref="ListingRequest.FollowsLink"/>). Its parent's listing said it was no link, and a junction
/// put at its name since would otherwise be followed, and its target listed under the child's path,
/// which the removal walk lists through too.</para>
/// </summary>
internal static unsafe partial class DirectoryQuery
{
    private const uint ListDirectory = 0x0001;
    private const uint Synchronize = 0x0010_0000;
    private const uint ShareEverything = 0x0001 | 0x0002 | 0x0004;
    private const uint FileOpen = 1;
    private const uint DirectoryFile = 0x0001;
    private const uint SynchronousIoNonAlert = 0x0020;
    private const uint OpenForBackupIntent = 0x4000;
    private const uint OpenReparsePoint = 0x0020_0000;

    /// <summary><c>OBJ_CASE_INSENSITIVE</c>: names are matched as <c>CreateFileW</c> matches them.</summary>
    private const uint CaseInsensitive = 0x0040;

    /// <summary><c>FILE_INFORMATION_CLASS</c>'s <c>FileFullDirectoryInformation</c>, what the runtime reads.</summary>
    private const int FileFullDirectoryInformation = 2;

    private const int StatusNoMoreFiles = unchecked((int)0x8000_0006);

    /// <summary>What a file system with no entry to return answers the first read, as for an empty volume root.</summary>
    private const int StatusNoSuchFile = unchecked((int)0xC000_000F);

    /// <summary>A name too long for the native string, refused before anything is opened.</summary>
    private static readonly ListingEnd NameTooLong = new(PathPresence.Refused, Denied: false, WhileReading: false);

    /// <param name="directory">
    /// In the extended form (§6.3). Every entry carries this string as its directory.
    /// </param>
    public static ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
    {
        // Windows writes up to this many bytes into the memory, so a larger ask would overrun it.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.BufferBytes, request.Memory.Bytes);

        if (NameToOpen(directory, request.Memory) is not { Length: > 0 } name)
        {
            return NameTooLong;
        }

        nint handle;
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
            status = NtCreateFile(
                &handle,
                ListDirectory | Synchronize,
                &attributes,
                &io,
                allocationSize: 0,
                fileAttributes: 0,
                ShareEverything,
                FileOpen,
                OpenOptions(request.FollowsLink),
                extendedAttributes: 0,
                extendedAttributesLength: 0);
        }

        if (status < 0)
        {
            return Ended(status, whileReading: false);
        }

        try
        {
            return Read(handle, directory, request, into);
        }
        finally
        {
            _ = NtClose(handle);
        }
    }

    /// <summary>
    /// The name <paramref name="directory"/> is opened by, written into the memory's own room, or
    /// nothing where it is too long for the native string, as <see cref="StatByName"/> refuses one.
    /// </summary>
    internal static ReadOnlySpan<char> NameToOpen(string directory, ListingMemory memory)
    {
        var extended = LongPath.Extended(directory);

        if (extended.Length > UnicodeString.LongestName)
        {
            return [];
        }

        var room = memory.NameRoom(extended.Length);

        return room.AsSpan(0, LongPath.WriteObjectManagerForm(extended, room));
    }

    /// <summary>The create options a directory is opened with. See <see cref="ListingRequest.FollowsLink"/>.</summary>
    internal static uint OpenOptions(bool followsLink) =>
        DirectoryFile | SynchronousIoNonAlert | OpenForBackupIntent | (followsLink ? 0 : OpenReparsePoint);

    /// <summary>How a listing that failed with <paramref name="status"/> ended.</summary>
    internal static ListingEnd Ended(int status, bool whileReading) =>
        ListingEnd.Failed(NtStatus.ToWin32Error(status), whileReading);

    private static ListingEnd Read(nint handle, string directory, ListingRequest request, List<WalkEntry> into)
    {
        var buffer = request.Memory.Entries;

        while (true)
        {
            IoStatusBlock io;
            var status = NtQueryDirectoryFile(
                handle,
                @event: 0,
                apcRoutine: 0,
                apcContext: 0,
                &io,
                buffer,
                (uint)request.BufferBytes,
                FileFullDirectoryInformation,
                returnSingleEntry: 0,
                fileName: null,
                restartScan: 0);

            // An answer of nothing would be asked again for ever, so it ends the listing too.
            if (status is StatusNoMoreFiles or StatusNoSuchFile || (status >= 0 && io.Information == 0))
            {
                return ListingEnd.Listed;
            }

            if (status < 0)
            {
                return Ended(status, whileReading: true);
            }

            Parse(buffer, directory, request.Files, into);
        }
    }

    private static void Parse(byte* buffer, string directory, FileNames files, List<WalkEntry> into)
    {
        var entry = (FileFullDirInformation*)buffer;

        while (true)
        {
            var name = new ReadOnlySpan<char>(&entry->FileName, (int)(entry->FileNameLength / sizeof(char)));
            var attributes = (FileAttributes)entry->FileAttributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);

            if (!IsSelfOrParent(name) && (isDirectory || files.KeepsFiles))
            {
                into.Add(new WalkEntry(
                    directory,
                    isDirectory || files.Names(name) ? new string(name) : null,
                    attributes,
                    isDirectory ? 0 : entry->EndOfFile,
                    DateTime.FromFileTimeUtc(entry->CreationTime),
                    DateTime.FromFileTimeUtc(entry->LastWriteTime)));
            }

            if (entry->NextEntryOffset == 0)
            {
                return;
            }

            entry = (FileFullDirInformation*)((byte*)entry + entry->NextEntryOffset);
        }
    }

    private static bool IsSelfOrParent(ReadOnlySpan<char> name) => name is "." or "..";

    /// <summary><c>FILE_FULL_DIR_INFORMATION</c>, its name running on past the end of the struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileFullDirInformation
    {
        public uint NextEntryOffset;
        public uint FileIndex;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long EndOfFile;
        public long AllocationSize;
        public uint FileAttributes;
        public uint FileNameLength;
        public uint EaSize;
        public char FileName;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(
        nint* file,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatus,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint extendedAttributes,
        uint extendedAttributesLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryDirectoryFile(
        nint file,
        nint @event,
        nint apcRoutine,
        nint apcContext,
        IoStatusBlock* ioStatus,
        void* fileInformation,
        uint length,
        int fileInformationClass,
        byte returnSingleEntry,
        UnicodeString* fileName,
        byte restartScan);

    [LibraryImport("ntdll.dll")]
    private static partial int NtClose(nint handle);
}
