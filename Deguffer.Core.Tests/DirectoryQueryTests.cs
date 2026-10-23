using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="DirectoryQuery"/>: the walk's listing from the disk, which must read each entry as the
/// runtime's enumerator read it, name the directory as §6.3 requires, and end each failure as the
/// enumerator's error ended it.
/// </summary>
public sealed class DirectoryQueryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ListingMemory _memory = new(WalkTuning.MinimumListingBuffer);

    public void Dispose()
    {
        _memory.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// Every entry with the name, attributes, length and times the runtime's enumerator gives, a hidden
    /// file, a read-only one, a folder and a link included, and none for the folder itself or its
    /// parent.
    /// </summary>
    [Fact]
    public void ListsEachEntryAsTheRuntimeListsIt()
    {
        var folder = _temp.CreateDirectory("folder");
        var data = _temp.CreateFile(1234, "folder", "data.bin");

        // Each time apart from the others, so a listing that read one in place of another is caught.
        File.SetCreationTimeUtc(data, new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(data, new DateTime(2020, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(data, new DateTime(2021, 11, 12, 13, 14, 15, DateTimeKind.Utc));
        File.SetAttributes(_temp.CreateFile(10, "folder", "hidden.bin"), FileAttributes.Hidden);
        File.SetAttributes(_temp.CreateFile(20, "folder", "locked.bin"), FileAttributes.ReadOnly);
        _temp.CreateFile(30, "folder", "sub", "below.bin");
        Junction.ToDirectory(Path.Combine(folder, "link"), _temp.CreateDirectory("elsewhere"));

        var listed = List(folder, followsLink: true, out var end);

        var expected = new DirectoryInfo(LongPath.Extended(folder))
            .EnumerateFileSystemInfos("*", DirectoryListing.EveryEntry)
            .OrderBy(info => info.Name, StringComparer.Ordinal)
            .Select(info => (
                info.Name,
                info.Attributes,
                Length: info is FileInfo file ? file.Length : 0,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc))
            .ToList();

        Assert.Equal(ListingEnd.Listed, end);
        Assert.Equal(["data.bin", "hidden.bin", "link", "locked.bin", "sub"], expected.Select(entry => entry.Name));
        Assert.Equal(
            expected,
            listed.OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => (entry.Name, entry.Attributes, entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc)));
    }

    /// <summary>
    /// §6.3: the directory is opened by its extended form under the object manager's prefix, a share's
    /// UNC segment kept, so the native call opens the folder a Win32 call would.
    /// </summary>
    [Theory]
    [InlineData(@"C:\cache\folder", @"\??\C:\cache\folder")]
    [InlineData(@"\\?\C:\cache\folder", @"\??\C:\cache\folder")]
    [InlineData(@"\\server\share\folder", @"\??\UNC\server\share\folder")]
    [InlineData(@"\\?\UNC\server\share\folder", @"\??\UNC\server\share\folder")]
    public void OpensTheDirectoryByItsObjectManagerName(string directory, string expected) =>
        Assert.Equal(expected, DirectoryQuery.NameToOpen(directory, _memory).ToString());

    /// <summary>
    /// A name too long for the native string is refused before anything is opened. Its length is
    /// counted in bytes in 16 bits, so one character past the limit wraps to a short count, and the
    /// call would open the start of the name: here, a real folder above the one asked for, whose
    /// entries would be handed back as the deeper folder's.
    /// </summary>
    [Fact]
    public void RefusesANameTooLongForTheNativeStringRatherThanOpenItsStart()
    {
        var above = LongPath.Extended(_temp.Path);
        _temp.CreateFile(10, "above.bin");

        // Wrapped, the count is exactly the length of the folder above's own name.
        var wrapsToAbove = Deeper(above, UnicodeString.LongestName + 1 + above.Length);
        var into = new List<WalkEntry>();

        var end = DirectoryQuery.List(wrapsToAbove, Request(followsLink: true), into);

        Assert.Equal(new ListingEnd(PathPresence.Refused, ListingRefusal.Other, WhileReading: false), end);
        Assert.Empty(into);
    }

    /// <summary>A name one character shorter than Windows refuses is still opened, and is simply not there.</summary>
    [Fact]
    public void OpensTheLongestNameWindowsTakes()
    {
        var longest = Deeper(LongPath.Extended(_temp.Path), UnicodeString.LongestName - 1);

        Assert.Equal(
            new ListingEnd(PathPresence.Absent, ListingRefusal.None, WhileReading: false),
            DirectoryQuery.List(longest, Request(followsLink: true), []));
    }

    /// <summary>
    /// §5.3: each status ends the listing as the Win32 error it maps to ended the runtime's, so a
    /// missing folder is absent, a refusal of access is denied, a file at the path is no directory, and
    /// anything else is refused.
    /// </summary>
    [Theory]
    [InlineData(0xC000_0034u, PathPresence.Absent, ListingRefusal.None)] // STATUS_OBJECT_NAME_NOT_FOUND
    [InlineData(0xC000_003Au, PathPresence.Absent, ListingRefusal.None)] // STATUS_OBJECT_PATH_NOT_FOUND
    [InlineData(0xC000_0022u, PathPresence.Refused, ListingRefusal.Denied)] // STATUS_ACCESS_DENIED
    [InlineData(0xC000_0043u, PathPresence.Refused, ListingRefusal.Other)] // STATUS_SHARING_VIOLATION
    [InlineData(0xC000_0103u, PathPresence.Refused, ListingRefusal.NotADirectory)] // STATUS_NOT_A_DIRECTORY
    [InlineData(0xC000_000Du, PathPresence.Refused, ListingRefusal.Other)] // STATUS_INVALID_PARAMETER, a buffer a server rejects
    public void EndsEachStatusAsTheWin32ErrorItMapsTo(uint status, PathPresence presence, ListingRefusal refusal)
    {
        Assert.Equal(new ListingEnd(presence, refusal, WhileReading: false), DirectoryQuery.Ended(unchecked((int)status), whileReading: false));
        Assert.Equal(new ListingEnd(presence, refusal, WhileReading: true), DirectoryQuery.Ended(unchecked((int)status), whileReading: true));
    }

    [Fact]
    public void EndsAMissingDirectoryAsAbsentAtTheOpen()
    {
        List(Path.Combine(_temp.Path, "nowhere"), followsLink: true, out var end);

        Assert.Equal(new ListingEnd(PathPresence.Absent, ListingRefusal.None, WhileReading: false), end);
    }

    /// <summary>
    /// A file is refused at the open, because the directory is opened as a directory. Opened any other
    /// way it is only refused once it is read, which a server rejecting a buffer looks like, and it would
    /// be listed again with a smaller one.
    /// </summary>
    [Fact]
    public void RefusesAFileAtTheOpen()
    {
        List(_temp.CreateFile(10, "a.bin"), followsLink: true, out var end);

        Assert.Equal(new ListingEnd(PathPresence.Refused, ListingRefusal.NotADirectory, WhileReading: false), end);
    }

    [Fact]
    public void EndsAFolderTheAccountMayNotListAsDenied()
    {
        var folder = _temp.CreateDirectory("denied");
        _temp.CreateFile(10, "denied", "a.bin");
        using var denied = new DeniedDirectory(folder);

        var listed = List(folder, followsLink: true, out var end);

        Assert.Equal(new ListingEnd(PathPresence.Refused, ListingRefusal.Denied, WhileReading: false), end);
        Assert.Empty(listed);
    }

    /// <summary>
    /// A folder another handle holds without sharing is refused at the open, where no buffer was used,
    /// so the walk does not list it again with a smaller one.
    /// </summary>
    [Fact]
    public void DoesNotListAgainAFolderRefusedByASharingViolation()
    {
        var folder = _temp.CreateDirectory("held");
        _temp.CreateFile(10, "held", "a.bin");
        using var held = new ExclusiveDirectory(folder);

        var asked = new List<int>();
        var largest = WalkTuning.MaximumListingBuffer;
        using var listing = new WalkListing(
            new ListingBuffer(largest),
            (directory, request, into) =>
            {
                asked.Add(request.BufferBytes);
                return DirectoryQuery.List(directory, request, into);
            },
            FileNames.All);

        var contents = listing.Read(LongPath.Extended(folder), followsLink: true);

        Assert.True(contents.WasRefused);
        Assert.Equal([largest], asked);
    }

    /// <summary>
    /// A root is listed through a link at its name, as it always was. A directory the walk descended
    /// into is opened as itself, so a link there lists nothing of what it points at.
    /// </summary>
    [Fact]
    public void ListsThroughAJunctionOnlyWhereItFollowsLinks()
    {
        var target = _temp.CreateDirectory("target");
        _temp.CreateFile(10, "target", "outside.bin");
        var link = Path.Combine(_temp.Path, "link");
        Junction.ToDirectory(link, target);

        var followed = List(link, followsLink: true, out var followedEnd);
        var itself = List(link, followsLink: false, out var itselfEnd);

        Assert.Equal(ListingEnd.Listed, followedEnd);
        Assert.Equal(["outside.bin"], followed.Select(entry => entry.Name));
        Assert.Equal(ListingEnd.Listed, itselfEnd);
        Assert.Empty(itself);
    }

    private List<WalkEntry> List(string directory, bool followsLink, out ListingEnd end)
    {
        var into = new List<WalkEntry>();
        end = DirectoryQuery.List(LongPath.Extended(directory), Request(followsLink), into);
        return into;
    }

    private ListingRequest Request(bool followsLink) =>
        new(_memory, _memory.Bytes, followsLink, FileNames.All);

    /// <summary><paramref name="root"/> with folders no disk holds added below it, to exactly <paramref name="length"/> characters.</summary>
    private static string Deeper(string root, int length)
    {
        var path = root;

        while (path.Length < length)
        {
            // Never so long that the last segment would be left one character, which a separator takes.
            var segment = Math.Min(200, length - path.Length - 1);
            if (length - path.Length - 1 - segment == 1)
            {
                segment--;
            }

            path += @"\" + new string('d', segment);
        }

        Assert.Equal(length, path.Length);
        return path;
    }
}
