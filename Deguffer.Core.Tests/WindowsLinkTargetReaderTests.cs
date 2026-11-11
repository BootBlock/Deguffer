using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Safety;
using Deguffer.Core.Startup;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the real reader reads from a link the file system holds: which kind it is, by its tag, and
/// where it points, in the form the target rule asks about. Real links in a scratch tree, because the
/// reparse data is what is under test. Shortcuts are read through the fake shell reader.
/// </summary>
public sealed class WindowsLinkTargetReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A junction records the object manager's <c>\??\</c> form. The target comes back in the ordinary
    /// form, or the rule would read it as a device path and call every junction unreachable.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void AFolderLinkIsReadAsItsKindWithItsTargetInOrdinaryForm(DirectoryLinkKind kind)
    {
        var target = _temp.CreateDirectory("elsewhere", "cache");
        var link = Path.Combine(_temp.CreateDirectory("profile"), "cache");
        DirectoryLink.Create(kind, link, target);

        var read = new WindowsLinkTargetReader(new FakeShortcutReader()).Read(link, isLink: true);

        Assert.NotNull(read);
        Assert.Equal(kind is DirectoryLinkKind.Junction ? LinkKind.Junction : LinkKind.SymbolicLink, read.Kind);
        Assert.Equal(LongPath.Display(target), read.Target);
    }

    /// <summary>A relative symbolic link is relative to the folder it is in, which is where Windows resolves it from.</summary>
    [Fact]
    public void ARelativeSymbolicLinkIsResolvedFromItsOwnFolder()
    {
        _temp.CreateFile(10, "tools", "tool.exe");
        var folder = _temp.CreateDirectory("bin");
        var link = Path.Combine(folder, "tool.exe");
        SymbolicLink.ToFile(link, @"..\tools\tool.exe");

        var read = new WindowsLinkTargetReader(new FakeShortcutReader()).Read(link, isLink: true);

        Assert.NotNull(read);
        Assert.Equal(LinkKind.SymbolicLink, read.Kind);
        Assert.Equal(Path.Combine(Final(_temp.Path), "tools", "tool.exe"), read.Target);
    }

    /// <summary>
    /// Windows follows the links above a relative link before it resolves it, so a link reached through
    /// a junction resolves from where its folder really is. Resolved from the path as written, a link
    /// in a profile moved by a junction would name a path that is not there, and read as broken.
    /// </summary>
    [Fact]
    public void ARelativeSymbolicLinkBehindAJunctionIsResolvedFromWhereItsFolderReallyIs()
    {
        _temp.CreateFile(10, "real", "tools", "tool.exe");
        var bin = _temp.CreateDirectory("real", "bin");
        SymbolicLink.ToFile(Path.Combine(bin, "tool.exe"), @"..\tools\tool.exe");
        var moved = Path.Combine(_temp.Path, "moved");
        Junction.ToDirectory(moved, Path.Combine(_temp.Path, "real"));

        var read = new WindowsLinkTargetReader(new FakeShortcutReader()).Read(Path.Combine(moved, "bin", "tool.exe"), isLink: true);

        Assert.Equal(Path.Combine(Final(_temp.Path), "real", "tools", "tool.exe"), read?.Target);
    }

    /// <summary>
    /// A link whose target has gone still records it, which is the whole point: the reader never
    /// follows the link, so a missing far side is no reason to read nothing.
    /// </summary>
    [Fact]
    public void ALinkWhoseTargetIsGoneStillReadsItsTarget()
    {
        var target = _temp.CreateDirectory("removed");
        var link = Path.Combine(_temp.Path, "link");
        Junction.ToDirectory(link, target);
        Directory.Delete(target);

        var read = new WindowsLinkTargetReader(new FakeShortcutReader()).Read(link, isLink: true);

        Assert.Equal(LongPath.Display(target), read?.Target);
    }

    [Fact]
    public void AShortcutIsReadThroughTheShellReaderAndOnlyByItsName()
    {
        var shortcut = _temp.CreateFile(0, "Old Game.lnk");
        var notOne = _temp.CreateFile(0, "Old Game.txt");
        var shell = new FakeShortcutReader()
            .With(shortcut, new ShortcutTarget(@"C:\Games\Old\game.exe", string.Empty))
            .With(notOne, new ShortcutTarget(@"C:\Games\Old\game.exe", string.Empty));
        var reader = new WindowsLinkTargetReader(shell);

        Assert.Equal(new LinkTarget(LinkKind.Shortcut, @"C:\Games\Old\game.exe"), reader.Read(shortcut, isLink: false));
        Assert.Null(reader.Read(notOne, isLink: false));
    }

    /// <summary>A shortcut to a shell folder names no file, and a file that will not load claims nothing.</summary>
    [Fact]
    public void AShortcutThatNamesNoFileReadsAsNothing()
    {
        var folderShortcut = _temp.CreateFile(0, "This PC.lnk");
        var broken = _temp.CreateFile(0, "Garbage.lnk");
        var reader = new WindowsLinkTargetReader(new FakeShortcutReader().With(folderShortcut, new ShortcutTarget(null, string.Empty)));

        Assert.Null(reader.Read(folderShortcut, isLink: false));
        Assert.Null(reader.Read(broken, isLink: false));
    }

    /// <summary>A plain folder handed over as a link is no link, and reads as nothing rather than as a guess.</summary>
    [Fact]
    public void AFolderThatIsNoLinkReadsAsNothing() =>
        Assert.Null(new WindowsLinkTargetReader(new FakeShortcutReader()).Read(_temp.CreateDirectory("plain"), isLink: true));

    /// <summary>Where <paramref name="folder"/> really is, as the reader resolves a relative link from it.</summary>
    private static string Final(string folder) => LongPath.Display(FileInformation.Default.FinalPath(folder)!);
}
