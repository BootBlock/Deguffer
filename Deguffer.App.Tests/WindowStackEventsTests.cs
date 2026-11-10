using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// Which window events make the About page's starfield ask again whether it is covered. Each event is
/// read after it was raised, so a window that closed by itself is gone by the time its hiding is read,
/// and that hiding must still count, or the field stays paused in front of the reader.
/// </summary>
public sealed class WindowStackEventsTests
{
    private const uint Hide = 0x8003;
    private const uint Reorder = 0x8004;
    private const uint LocationChange = 0x800B;

    [Theory]
    [InlineData(Hide)]
    [InlineData(LocationChange)]
    public void ATopLevelWindowsEventCounts(uint kind) =>
        Assert.True(WindowStackEvents.Concerns(kind, EventWindow.TopLevel));

    /// <summary>A covering window that closed is gone by the time its hiding is read, and it uncovered the field.</summary>
    [Fact]
    public void TheHidingOfAWindowThatHasGoneCounts() =>
        Assert.True(WindowStackEvents.Concerns(Hide, EventWindow.Gone));

    [Theory]
    [InlineData(Hide)]
    [InlineData(LocationChange)]
    public void AWindowInsideAnotherDoesNotCount(uint kind) =>
        Assert.False(WindowStackEvents.Concerns(kind, EventWindow.Inner));

    /// <summary>The desktop reorders the windows on it, and nothing else it raises moves one.</summary>
    [Fact]
    public void OnlyTheDesktopsReorderCounts()
    {
        Assert.True(WindowStackEvents.Concerns(Reorder, EventWindow.Desktop));
        Assert.False(WindowStackEvents.Concerns(LocationChange, EventWindow.Desktop));
    }
}
