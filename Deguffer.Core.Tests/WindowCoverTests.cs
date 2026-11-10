using Deguffer.Core.Viewing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which of the windows in front of Deguffer's hide what is behind them, and that every doubt answers
/// "seen": a field left moving behind a window costs processor time, where one stopped in front of the
/// reader is a fault they can see.
/// </summary>
public sealed class WindowCoverTests
{
    private const nint Ours = 1;

    private static readonly ScreenRect Field = new(100, 100, 500, 400);
    private static readonly ScreenRect OverAll = new(0, 0, 800, 600);

    private static FakeStackedWindow Ourselves => new(Ours, new ScreenRect(50, 50, 550, 450));

    [Fact]
    public void ASolidWindowInFrontHidesIt() =>
        Assert.True(Hides(new FakeWindowStack().Add(new(2, OverAll)).Add(Ourselves)));

    /// <summary>A window behind Deguffer's hides nothing of it, however much it overlaps.</summary>
    [Fact]
    public void AWindowBehindHidesNothing() =>
        Assert.False(Hides(new FakeWindowStack().Add(Ourselves).Add(new(2, OverAll))));

    /// <summary>The ways a window in front can leave what is behind it to be seen, or leave that unknown.</summary>
    public enum InFront
    {
        Hidden,
        Minimised,
        Cloaked,
        SeeThrough,
        CloakUnreadable,
        FrameUnreadable,
    }

    /// <summary>
    /// A window that is not shown, or shows what is behind it, covers nothing. One whose cloak or frame
    /// cannot be read covers nothing either, rather than stopping a field the reader can see.
    /// </summary>
    [Theory]
    [InlineData(InFront.Hidden)]
    [InlineData(InFront.Minimised)]
    [InlineData(InFront.Cloaked)]
    [InlineData(InFront.SeeThrough)]
    [InlineData(InFront.CloakUnreadable)]
    [InlineData(InFront.FrameUnreadable)]
    public void AWindowThatCoversNothingHidesNothing(InFront kind)
    {
        var window = new FakeStackedWindow(2, OverAll);

        window = kind switch
        {
            InFront.Hidden => window with { Visible = false },
            InFront.Minimised => window with { Minimised = true },
            InFront.Cloaked => window with { Cloaked = true },
            InFront.SeeThrough => window with { Solid = false },
            InFront.CloakUnreadable => window with { Cloaked = null },
            _ => window with { Frame = null },
        };

        Assert.False(Hides(new FakeWindowStack().Add(window).Add(Ourselves)));
    }

    /// <summary>Deguffer's own window on another virtual desktop cannot be seen, whatever is in front of it.</summary>
    [Fact]
    public void OurWindowCloakedIsHidden() =>
        Assert.True(Hides(new FakeWindowStack().Add(Ourselves with { Cloaked = true })));

    [Fact]
    public void ADesktopThatCannotBeEnumeratedHidesNothing() =>
        Assert.False(Hides(new FakeWindowStack { Enumerates = false }.Add(new(2, OverAll)).Add(Ourselves)));

    [Fact]
    public void MonitorsThatCannotBeListedHideNothing() =>
        Assert.False(Hides(new FakeWindowStack { Monitors = null }.Add(new(2, OverAll)).Add(Ourselves)));

    [Fact]
    public void NoMonitorsAtAllHideNothing() =>
        Assert.False(Hides(new FakeWindowStack { Monitors = [] }.Add(Ourselves)));

    /// <summary>A window that is gone from the enumeration has nothing in front of it to read.</summary>
    [Fact]
    public void AWindowNotInTheEnumerationIsSeen() =>
        Assert.False(Hides(new FakeWindowStack().Add(new(2, OverAll))));

    [Fact]
    public void TheFieldOffEveryScreenIsHidden() =>
        Assert.True(WindowCover.Hides(new FakeWindowStack().Add(Ourselves), Ours, new ScreenRect(2000, 100, 2400, 400)));

    /// <summary>Two windows in front that each cover half of the field hide it between them.</summary>
    [Fact]
    public void WindowsInFrontHideItBetweenThem() =>
        Assert.True(Hides(new FakeWindowStack()
            .Add(new(2, new ScreenRect(0, 0, 300, 600)))
            .Add(new(3, new ScreenRect(300, 0, 800, 600)))
            .Add(Ourselves)));

    /// <summary>Once the field is hidden, nothing further back in front of it is read.</summary>
    [Fact]
    public void AHiddenFieldStopsReadingWindows()
    {
        var stack = new FakeWindowStack().Add(new(2, OverAll)).Add(new(3, OverAll)).Add(new(4, OverAll)).Add(Ourselves);

        Assert.True(Hides(stack));
        Assert.Equal(1, stack.Reads);
    }

    private static bool Hides(FakeWindowStack stack) => WindowCover.Hides(stack, Ours, Field);
}
