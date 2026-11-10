using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether anything of a rectangle is left to see once it is cut to the screens and the windows in
/// front of it are taken away. A pixel left over means it is seen.
/// </summary>
public sealed class ScreenCoverTests
{
    private static readonly ScreenRect[] OneScreen = [new(0, 0, 1920, 1080)];
    private static readonly ScreenRect Target = new(100, 100, 500, 400);

    [Fact]
    public void NothingInFrontLeavesItSeen() =>
        Assert.False(ScreenCover.IsHidden(Target, OneScreen, []));

    [Fact]
    public void OneWindowOverAllOfItHidesIt() =>
        Assert.True(ScreenCover.IsHidden(Target, OneScreen, [new(0, 0, 800, 600)]));

    [Fact]
    public void AWindowExactlyOverItHidesIt() =>
        Assert.True(ScreenCover.IsHidden(Target, OneScreen, [Target]));

    /// <summary>One pixel column left at the right edge is enough to see it by.</summary>
    [Fact]
    public void OnePixelLeftUncoveredLeavesItSeen() =>
        Assert.False(ScreenCover.IsHidden(Target, OneScreen, [Target with { Right = 499 }]));

    /// <summary>Windows that each cover part of it, and none of them all of it, can hide it between them.</summary>
    [Fact]
    public void WindowsThatTileItHideItBetweenThem() =>
        Assert.True(ScreenCover.IsHidden(
            Target,
            OneScreen,
            [new(0, 0, 300, 250), new(300, 0, 900, 250), new(0, 250, 260, 900), new(250, 240, 900, 900)]));

    [Fact]
    public void AHoleBetweenTheWindowsLeavesItSeen() =>
        Assert.False(ScreenCover.IsHidden(
            Target,
            OneScreen,
            [new(0, 0, 900, 200), new(0, 300, 900, 900), new(0, 200, 250, 300), new(260, 200, 900, 300)]));

    [Fact]
    public void AWindowBesideItHidesNothing() =>
        Assert.False(ScreenCover.IsHidden(Target, OneScreen, [new(500, 100, 900, 400)]));

    /// <summary>Nobody can see a part of a window that is off every screen.</summary>
    [Fact]
    public void OffEveryScreenItIsHidden() =>
        Assert.True(ScreenCover.IsHidden(new(2000, 100, 2400, 400), OneScreen, []));

    /// <summary>Half on the screen, it is seen by the half that is, and hidden once that half is covered.</summary>
    [Fact]
    public void OnlyThePartOnAScreenNeedsCovering()
    {
        var straddling = new ScreenRect(1700, 100, 2100, 400);

        Assert.False(ScreenCover.IsHidden(straddling, OneScreen, []));
        Assert.True(ScreenCover.IsHidden(straddling, OneScreen, [new(1600, 0, 1920, 500)]));
    }

    /// <summary>Across two monitors, a window over the part on one leaves the part on the other seen.</summary>
    [Fact]
    public void EachMonitorsPartIsSeenOnItsOwn()
    {
        ScreenRect[] two = [new(0, 0, 1920, 1080), new(1920, 0, 3840, 1080)];
        var spanning = new ScreenRect(1800, 100, 2100, 400);

        Assert.False(ScreenCover.IsHidden(spanning, two, [new(1700, 0, 1920, 500)]));
        Assert.True(ScreenCover.IsHidden(spanning, two, [new(1700, 0, 1920, 500), new(1920, 0, 2200, 500)]));
    }

    /// <summary>
    /// A desktop of many small windows that cut what is left into more pieces than it will follow is
    /// given up as seen, which only leaves the field doing what it did before it asked.
    /// </summary>
    [Fact]
    public void TooManyPiecesToFollowIsTakenAsSeen()
    {
        var target = new ScreenRect(0, 0, 1000, 1000);
        var dots = new List<ScreenRect>();

        for (var x = 1; x < 1000; x += 10)
        {
            for (var y = 1; y < 1000; y += 10)
            {
                dots.Add(new ScreenRect(x, y, x + 2, y + 2));
            }
        }

        dots.Add(target);

        Assert.False(ScreenCover.IsHidden(target, [target], dots));
    }
}
