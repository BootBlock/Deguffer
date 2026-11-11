using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The report of broken shortcuts in the Start menu, on the desktop and in the Startup folders: the
/// folders are real ones in a scratch tree, so the listing is the one the app runs, and every shortcut
/// and link in them is described through the fakes, so no shell writes one and no target is real.
/// </summary>
public sealed class ShortcutFolderReportTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeLinkTargets _links = new();
    private readonly FakePathProbe _probe = new();

    public void Dispose() => _temp.Dispose();

    private LinkInspection Inspection() => new(_links, new LinkTargets(_probe, new FakeVolumeInventory().With(@"C:\")));

    [Fact]
    public void ShortcutsWhoseTargetIsGoneAreReportedAtAnyDepthByPath()
    {
        var startMenu = _temp.CreateDirectory("Start Menu");
        var desktop = _temp.CreateDirectory("Desktop");
        var gone = Shortcut(@"C:\Games\Old\game.exe", "Start Menu", "Programs", "Old Game", "Old Game.lnk");
        var kept = Shortcut(@"C:\Games\Kept\game.exe", "Start Menu", "Programs", "Kept.lnk");
        var away = Shortcut(@"E:\Games\game.exe", "Desktop", "Away.lnk");
        var alsoGone = Shortcut(@"C:\Tools\tool.exe", "Desktop", "Tool.lnk");
        _temp.CreateFile(10, "Desktop", "notes.txt");
        _probe.File(@"C:\Games\Kept\game.exe");

        var report = ShortcutFolderReport.Read([startMenu, desktop], Inspection());

        Assert.Equal([alsoGone, gone], report.Links.Select(link => link.Path));
        Assert.All(report.Links, link => Assert.True(link.IsOffered));
        Assert.Equal(1, report.Unreachable);
        Assert.Empty(report.Unread);
        Assert.True(_links.Reads.ContainsKey(kept));
        Assert.True(_links.Reads.ContainsKey(away));
        Assert.False(_links.Reads.ContainsKey(Path.Combine(desktop, "notes.txt")));
    }

    /// <summary>
    /// A junction in the Start menu is reported as the link it is, and never descended into: a shortcut
    /// on its far side is in some other folder, which this report was not asked about.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ALinkIsReportedAndNeverDescendedInto(DirectoryLinkKind kind)
    {
        var startMenu = _temp.CreateDirectory("Start Menu");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        var behind = Shortcut(@"C:\Behind\app.exe", "elsewhere", "Behind.lnk");
        var link = Path.Combine(startMenu, "Moved");
        DirectoryLink.Create(kind, link, elsewhere);
        _links.With(link, kind is DirectoryLinkKind.Junction ? LinkKind.Junction : LinkKind.SymbolicLink, @"C:\Moved\away");

        var report = ShortcutFolderReport.Read([startMenu], Inspection());

        var reported = Assert.Single(report.Links);
        Assert.Equal(link, reported.Path);
        Assert.True(reported.IsDirectory);
        Assert.False(_links.Reads.ContainsKey(behind));
    }

    /// <summary>A folder that is not there holds nothing to report, and is not one that could not be read.</summary>
    [Fact]
    public void AFolderThatIsNotThereIsNeitherReportedNorUnread()
    {
        var report = ShortcutFolderReport.Read([Path.Combine(_temp.Path, "never-made")], Inspection());

        Assert.Empty(report.Links);
        Assert.Empty(report.Unread);
    }

    /// <summary>A refusal is never absence: a folder Windows will not list is named, so a short report says so.</summary>
    [Fact]
    public void AFolderWindowsWillNotListIsNamed()
    {
        var desktop = _temp.CreateDirectory("Desktop");
        var locked = _temp.CreateDirectory("Desktop", "Locked");

        using (new DeniedDirectory(locked))
        {
            var report = ShortcutFolderReport.Read([desktop], Inspection());

            Assert.Equal([locked], report.Unread);
        }
    }

    [Fact]
    public void EachFolderIsNamedOnceAndOneInsideAnotherIsLeftOut()
    {
        var user = new FakeUserEnvironment(_temp.Path);
        var machine = new FakeSystemDirectories(_temp.Path);

        var folders = ShortcutFolders.Of(user, machine);

        // The Startup folders are inside the Start menu folders where Windows puts them.
        Assert.Equal(
            [user.StartMenuFolder!, machine.CommonStartMenuFolder!, user.DesktopFolder!, machine.CommonDesktopFolder!],
            folders);
    }

    /// <summary>An empty shortcut file at <paramref name="parts"/> that the fake says points at <paramref name="target"/>.</summary>
    private string Shortcut(string target, params string[] parts)
    {
        var path = _temp.CreateFile(0, parts);
        _links.With(path, LinkKind.Shortcut, target);
        return LongPath.Display(path);
    }
}
