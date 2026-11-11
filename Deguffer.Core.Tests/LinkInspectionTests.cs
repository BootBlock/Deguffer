using Deguffer.Core.Exploring.Links;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A link read and its target asked about together: which links are reported, which of those are
/// offered, and the look again a removal stands on. Through the fakes: no shortcut or link is real.
/// </summary>
public sealed class LinkInspectionTests
{
    private const string Shortcut = @"C:\Users\testuser\Desktop\Old Game.lnk";
    private const string Junction = @"C:\Users\testuser\cache";

    private readonly FakeLinkTargets _links = new();
    private readonly FakePathProbe _probe = new();
    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\");

    private LinkInspection Inspection() => new(_links, new LinkTargets(_probe, _volumes));

    [Fact]
    public void AShortcutWhoseTargetIsGoneIsReportedAndOffered()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe");

        var link = Inspection().Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 1_200);

        Assert.NotNull(link);
        Assert.Equal(
            (Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe", LinkTargetState.Gone),
            (link.Path, link.Kind, link.Target, link.State));
        Assert.True(link.IsOffered);
        Assert.Equal(1_200, link.Bytes);
    }

    [Fact]
    public void AJunctionWhoseTargetIsGoneIsReportedAsAFolderAndOffered()
    {
        _links.With(Junction, LinkKind.Junction, @"C:\Moved\cache");

        var link = Inspection().Inspect(Junction, isLink: true, isDirectory: true, bytes: 0);

        Assert.NotNull(link);
        Assert.Equal(LinkKind.Junction, link.Kind);
        Assert.True(link.IsDirectory);
        Assert.True(link.IsOffered);
    }

    [Fact]
    public void ALinkWhoseTargetIsThereIsNotReported()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Kept\game.exe");
        _probe.File(@"C:\Games\Kept\game.exe");

        Assert.Null(Inspection().Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0));
    }

    /// <summary>Reported, so the list can count it, and never offered: the drive may come back.</summary>
    [Fact]
    public void ALinkToADriveThatIsNotThereIsReportedAndNeverOffered()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"E:\Games\game.exe");

        var link = Inspection().Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0);

        Assert.NotNull(link);
        Assert.Equal(LinkTargetState.Unreachable, link.State);
        Assert.False(link.IsOffered);
    }

    /// <summary>A shortcut to a shell folder, or a file that will not load as one, claims nothing.</summary>
    [Fact]
    public void ALinkThatRecordsNoTargetIsNotReported() =>
        Assert.Null(Inspection().Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0));

    /// <summary>G4: a filter typed a letter at a time asks again, and the shortcut is loaded once.</summary>
    [Fact]
    public void ALinkIsReadOncePerInstance()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe");
        var inspection = Inspection();

        inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0);
        inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0);

        Assert.Equal(1, _links.Reads[Shortcut]);
    }

    [Fact]
    public void ALinkWhoseTargetIsStillGoneIsStillGone()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe");
        var inspection = Inspection();
        var link = inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0)!;

        Assert.True(inspection.IsStillGone(link));
    }

    /// <summary>A program reinstalled since the list was made has put the target back.</summary>
    [Fact]
    public void ALinkWhoseTargetCameBackIsNotStillGone()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe");
        var inspection = Inspection();
        var link = inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0)!;

        _probe.File(@"C:\Games\Old\game.exe");

        Assert.False(inspection.IsStillGone(link));
    }

    /// <summary>A shortcut rewritten to point somewhere else is not the shortcut that was offered.</summary>
    [Fact]
    public void ALinkThatNowPointsSomewhereElseIsNotStillGone()
    {
        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\Old\game.exe");
        var inspection = Inspection();
        var link = inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0)!;

        _links.With(Shortcut, LinkKind.Shortcut, @"C:\Games\New\game.exe");

        Assert.False(inspection.IsStillGone(link));
    }

    /// <summary>A drive unplugged since the list was made leaves the target unreachable, not gone.</summary>
    [Fact]
    public void ALinkWhoseDriveWentAwayIsNotStillGone()
    {
        _volumes.With(@"D:\");
        _links.With(Shortcut, LinkKind.Shortcut, @"D:\Games\game.exe");
        var inspection = Inspection();
        var link = inspection.Inspect(Shortcut, isLink: false, isDirectory: false, bytes: 0)!;
        Assert.True(link.IsOffered);

        _volumes.Without(@"D:\");

        Assert.False(inspection.IsStillGone(link));
    }
}
