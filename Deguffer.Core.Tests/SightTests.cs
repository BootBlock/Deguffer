using Deguffer.Core.Viewing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether anyone can see a part of one of Deguffer's windows, which decides whether the Memory page reads
/// the machine and whether the About page's starfield flies. Every doubt answers "seen": work left going
/// behind a window costs processor time, where work stopped in front of the reader is a fault they can see.
/// </summary>
public sealed class SightTests
{
    private const nint Ours = 1;

    private static readonly ScreenRect Page = new(100, 100, 500, 400);
    private static readonly ScreenRect OverAll = new(0, 0, 800, 600);

    private static FakeStackedWindow Ourselves => new(Ours, new ScreenRect(50, 50, 550, 450));

    [Fact]
    public void ALoadedPartInAShownWindowWithNothingInFrontIsSeen() =>
        Assert.Equal(Sight.Seen, Sights.Of(loaded: true, hostVisible: true, Cover(new FakeWindowStack().Add(Ourselves))));

    /// <summary>
    /// A window covered by others is shown as far as the XAML host knows, and is not seen all the same.
    /// </summary>
    [Fact]
    public void APartBehindASolidWindowIsCovered() =>
        Assert.Equal(
            Sight.Covered,
            Sights.Of(loaded: true, hostVisible: true, Cover(new FakeWindowStack().Add(new(2, OverAll)).Add(Ourselves))));

    /// <summary>A desktop whose windows cannot be read hides nothing, so a page in front of the reader goes on reading.</summary>
    [Fact]
    public void APartOnADesktopThatCannotBeReadIsSeen() =>
        Assert.Equal(
            Sight.Seen,
            Sights.Of(
                loaded: true,
                hostVisible: true,
                Cover(new FakeWindowStack { Enumerates = false }.Add(new(2, OverAll)).Add(Ourselves))));

    /// <summary>A part in no host that can say whether its window is shown is taken as shown.</summary>
    [Fact]
    public void APartWhoseHostCannotSayIsSeen() =>
        Assert.Equal(Sight.Seen, Sights.Of(loaded: true, hostVisible: null, () => false));

    /// <summary>
    /// A page that has gone, or a window minimised or hidden, is not seen, and nothing in front of it is
    /// read to say so: answering the cover reads every window in front.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, null)]
    [InlineData(true, false)]
    public void APartNotShownIsUnseenWithoutReadingTheDesktop(bool loaded, bool? hostVisible)
    {
        var asked = false;

        Assert.Equal(Sight.Unseen, Sights.Of(loaded, hostVisible, () => asked = true));
        Assert.False(asked);
    }

    private static Func<bool> Cover(FakeWindowStack stack) => () => WindowCover.Hides(stack, Ours, Page);
}
