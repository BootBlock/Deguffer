using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="EntryDeletion"/>, through the real file system in a folder of the test's own: a
/// read-only entry goes in one call and a refused one is left exactly as it was, a link goes as itself
/// and never reaches what it names, and every refusal throws what <see cref="File.Delete"/> or
/// <see cref="Directory.Delete(string)"/> throws for the same case, because the removal's callers read
/// nothing else.
/// </summary>
public sealed class EntryDeletionTests : IDisposable
{
    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>, a name surrogate.</summary>
    private const uint SymbolicLinkTag = 0xA000_000C;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction, or a folder a volume is mounted at.</summary>
    private const uint MountPointTag = 0xA000_0003;

    /// <summary><c>IO_REPARSE_TAG_CLOUD_6</c>, a OneDrive placeholder: no name surrogate.</summary>
    private const uint CloudTag = 0x9000_601A;

    /// <summary><c>IO_REPARSE_TAG_DEDUP</c>: no name surrogate.</summary>
    private const uint DeduplicatedTag = 0x8000_0013;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void DeletesAReadOnlyFile()
    {
        var file = _temp.CreateFile(64, "locked.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        EntryDeletion.DeleteFile(LongPath.Extended(file));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeletesAReadOnlyEmptyFolder()
    {
        var folder = _temp.CreateDirectory("locked");
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);

        EntryDeletion.DeleteDirectory(LongPath.Extended(folder));

        Assert.False(Directory.Exists(folder));
    }

    /// <summary>
    /// A refusal changes nothing. Clearing the bit and then deleting left a read-only file another
    /// program holds with its bit cleared, a change to a file the removal was leaving standing.
    /// </summary>
    [Fact]
    public void LeavesAHeldReadOnlyFileWithItsBitStillSet()
    {
        var file = _temp.CreateFile(64, "held.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var changed = ChangeTimeOf(file);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var refused = Assert.ThrowsAny<IOException>(() => EntryDeletion.DeleteFile(LongPath.Extended(file)));

            Assert.Equal(RefusalReason.InUse, RefusalReasons.Of(refused));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly), "a refused file had its read-only bit cleared");
        }

        // Not changed even for a moment: clearing the bit and setting it again moves the time Windows
        // keeps for a change to the file's metadata, which nothing else here touches.
        Assert.Equal(changed, ChangeTimeOf(file));
        File.SetAttributes(file, FileAttributes.Normal);
    }

    /// <summary>
    /// A folder an access rule refuses is refused as <see cref="Directory.Delete(string)"/> refuses it:
    /// a bare <see cref="IOException"/>, which <see cref="Execution.DirectoryDeletion"/> reads as denied.
    /// </summary>
    [Fact]
    public void RefusesAFolderAnAccessRuleGuardsAsDirectoryDeleteDoes()
    {
        var folder = _temp.CreateDirectory("holder", "guarded");

        using (new UndeletableFolder(folder))
        {
            var expected = Record.Exception(() => Directory.Delete(LongPath.Extended(folder)));
            var actual = Record.Exception(() => EntryDeletion.DeleteDirectory(LongPath.Extended(folder)));

            AssertSameRefusal(expected, actual);
            Assert.Equal(RefusalReason.Denied, RefusalReasons.Of(actual!));
            Assert.True(Directory.Exists(folder));
        }
    }

    /// <summary>
    /// The same where an access rule refuses it: the retry that cleared the bit and tried again ran on
    /// every access refusal, and changed the attributes of a file Windows was never going to let go.
    /// </summary>
    [Fact]
    public void LeavesAReadOnlyFileAnAccessRuleRefusesWithItsBitStillSet()
    {
        var file = _temp.CreateFile(64, "guarded.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        using (new UndeletableFile(file))
        {
            var refused = Record.Exception(() => EntryDeletion.DeleteFile(LongPath.Extended(file)));

            Assert.IsType<UnauthorizedAccessException>(refused);
            Assert.Equal(RefusalReason.Denied, RefusalReasons.Of(refused));
            Assert.True(File.Exists(file), "a file an access rule refuses was deleted");
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly), "a refused file had its read-only bit cleared");
        }

        File.SetAttributes(file, FileAttributes.Normal);
    }

    /// <summary>
    /// The target is empty, so a deletion that followed the link would delete the target itself and
    /// succeed. A target with something in it refuses that deletion, and could not tell the two apart.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void RemovesALinkToAnEmptyFolderAsItselfAndLeavesItsTarget(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("precious");
        var link = Path.Combine(_temp.Path, "link");
        DirectoryLink.Create(kind, link, outside);

        EntryDeletion.DeleteDirectory(LongPath.Extended(link));

        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(outside), "deleting the link deleted the folder it points at");
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void RemovesAReadOnlyLinkToAFolderAsItself(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(64, "precious", "irreplaceable.bin");
        var link = Path.Combine(_temp.Path, "link");
        DirectoryLink.Create(kind, link, outside);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);

        EntryDeletion.DeleteDirectory(LongPath.Extended(link));

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(bystander), "deleting the link reached what it points at");
        Assert.False(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void RemovesALinkToAFileAsItselfAndLeavesItsTarget()
    {
        var target = _temp.CreateFile(64, "precious.bin");
        var link = Path.Combine(_temp.Path, "link.bin");
        SymbolicLink.ToFile(link, target);

        EntryDeletion.DeleteFile(LongPath.Extended(link));

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(target), "deleting the link deleted the file it points at");
    }

    /// <summary>
    /// A link to a folder asked for as a file is refused, as <see cref="File.Delete"/> refuses it, and
    /// stays. <see cref="Execution.FileRemover"/> reports such a link as refused on that answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void RefusesALinkToAFolderAskedForAsAFile(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("precious");
        var link = Path.Combine(_temp.Path, "link");
        DirectoryLink.Create(kind, link, outside);

        var expected = Record.Exception(() => File.Delete(LongPath.Extended(link)));
        var actual = Record.Exception(() => EntryDeletion.DeleteFile(LongPath.Extended(link)));

        Assert.IsType<UnauthorizedAccessException>(actual);
        AssertSameRefusal(expected, actual);
        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void DeletesAnEntryBeyondMaxPathByItsExtendedForm()
    {
        var deep = _temp.Path;

        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var file = LongPath.Extended(Path.Combine(deep, "payload.bin"));
        File.WriteAllBytes(file, [1]);

        EntryDeletion.DeleteFile(file);
        EntryDeletion.DeleteDirectory(LongPath.Extended(deep));

        Assert.False(Directory.Exists(LongPath.Extended(deep)));
    }

    /// <summary>The cases a file deletion is refused in, each arranged by its name.</summary>
    public static TheoryData<string> FileCases => ["missing", "missing parent", "a folder", "held"];

    [Theory]
    [MemberData(nameof(FileCases))]
    public void RefusesAFileAsFileDeleteDoes(string @case)
    {
        var path = Arrange(@case, out var held);

        using (held)
        {
            var expected = Record.Exception(() => File.Delete(path));
            var actual = Record.Exception(() => EntryDeletion.DeleteFile(path));

            AssertSameRefusal(expected, actual);
        }
    }

    /// <summary>The cases a directory deletion is refused in.</summary>
    public static TheoryData<string> DirectoryCases => ["missing", "missing parent", "a file", "full"];

    [Theory]
    [MemberData(nameof(DirectoryCases))]
    public void RefusesAFolderAsDirectoryDeleteDoes(string @case)
    {
        var path = Arrange(@case, out var held);

        using (held)
        {
            var expected = Record.Exception(() => Directory.Delete(path));
            var actual = Record.Exception(() => EntryDeletion.DeleteDirectory(path));

            AssertSameRefusal(expected, actual);
        }
    }

    /// <summary>
    /// A full folder is refused with <c>ERROR_DIR_NOT_EMPTY</c>, which is how
    /// <see cref="Execution.DirectoryDeletion"/> tells a folder held up by its contents from one refused
    /// for itself, and a read-only one keeps its bit.
    /// </summary>
    [Fact]
    public void RefusesAFullReadOnlyFolderAsNotEmptyAndLeavesItsBit()
    {
        var path = Arrange("full", out _);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var refused = Assert.IsType<IOException>(Record.Exception(() => EntryDeletion.DeleteDirectory(path)));

        Assert.Equal(Win32Error.DirectoryNotEmpty, refused.HResult & 0xFFFF);
        Assert.True(Directory.Exists(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(path, FileAttributes.Directory);
    }

    public static TheoryData<uint?, uint, bool, string> Routes => new()
    {
        // Ordinary entries, asked for as what they are, go in one call.
        { (uint)FileAttributes.Archive, 0, false, nameof(DeletionRoute.OneCall) },
        { (uint)(FileAttributes.ReadOnly | FileAttributes.Archive), 0, false, nameof(DeletionRoute.OneCall) },
        { (uint)FileAttributes.Directory, 0, true, nameof(DeletionRoute.OneCall) },

        // A symbolic link is removed as itself through the handle.
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Archive), SymbolicLinkTag, false, nameof(DeletionRoute.OneCall) },
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory), SymbolicLinkTag, true, nameof(DeletionRoute.OneCall) },

        // A mount point, a junction included, goes to the runtime, which tells the mount manager.
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory), MountPointTag, true, nameof(DeletionRoute.Runtime) },

        // A reparse point that names nothing is its filter's, which only the runtime's call reaches.
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Archive), CloudTag, false, nameof(DeletionRoute.Runtime) },
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Archive), DeduplicatedTag, false, nameof(DeletionRoute.Runtime) },
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory), CloudTag, true, nameof(DeletionRoute.Runtime) },

        // The other kind is refused, a link to a folder asked for as a file among them.
        { (uint)FileAttributes.Directory, 0, false, nameof(DeletionRoute.WrongKind) },
        { (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory), SymbolicLinkTag, false, nameof(DeletionRoute.WrongKind) },
        { (uint)FileAttributes.Archive, 0, true, nameof(DeletionRoute.WrongKind) },

        // Windows would not describe it: its own call decides.
        { null, 0, false, nameof(DeletionRoute.Runtime) },
        { null, 0, true, nameof(DeletionRoute.Runtime) },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void RoutesEachEntryByWhatWindowsSaidItIs(uint? attributes, uint tag, bool asDirectory, string expected)
    {
        FileInformation.FileAttributeTagInfo? described = attributes is { } known
            ? new FileInformation.FileAttributeTagInfo { FileAttributes = known, ReparseTag = tag }
            : null;

        Assert.Equal(expected, EntryDeletion.RouteFor(described, asDirectory).ToString());
    }

    private static long ChangeTimeOf(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(FileInformation.TryBasic(handle, out var basic));
        return basic.ChangeTime;
    }

    private static void AssertSameRefusal(Exception? expected, Exception? actual)
    {
        Assert.Equal(expected?.GetType(), actual?.GetType());
        Assert.Equal(expected?.HResult, actual?.HResult);
    }

    /// <summary>The extended path for <paramref name="case"/>, and a handle held open where the case needs one.</summary>
    private string Arrange(string @case, out IDisposable? held)
    {
        held = null;

        switch (@case)
        {
            case "missing":
                return LongPath.Extended(Path.Combine(_temp.Path, "nothing"));

            case "missing parent":
                return LongPath.Extended(Path.Combine(_temp.Path, "no-folder", "nothing"));

            case "a folder":
                return LongPath.Extended(_temp.CreateDirectory("folder"));

            case "a file":
                return LongPath.Extended(_temp.CreateFile(8, "file.bin"));

            case "full":
                _temp.CreateFile(8, "full", "inside.bin");
                return LongPath.Extended(Path.Combine(_temp.Path, "full"));

            case "held":
                var file = _temp.CreateFile(8, "held.bin");
                held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                return LongPath.Extended(file);

            default:
                throw new ArgumentOutOfRangeException(nameof(@case), @case, "Not a case this test arranges.");
        }
    }
}
