using Deguffer.Core.Configuration;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rail folds to its icons on a narrow window and opens on a wide one, unless the reader said
/// otherwise with its toggle, and the toggle is also how a reader hands it back to the width.
/// </summary>
public sealed class RailFoldTests
{
    private const double Narrow = 640;
    private const double Wide = 1200;

    [Theory]
    [InlineData(Narrow, false)]
    [InlineData(RailFold.OpenWidth - 1, false)]
    [InlineData(RailFold.OpenWidth, true)]
    [InlineData(Wide, true)]
    public void NobodysChoiceFollowsTheWidth(double width, bool open) =>
        Assert.Equal(open, RailFold.IsOpen(RailChoice.Automatic, width));

    [Theory]
    [InlineData(Narrow)]
    [InlineData(Wide)]
    public void TheReadersChoiceHoldsAtEveryWidth(double width)
    {
        Assert.True(RailFold.IsOpen(RailChoice.Open, width));
        Assert.False(RailFold.IsOpen(RailChoice.Collapsed, width));
    }

    /// <summary>The first launch's window is 1000 pixels wide, and a new reader sees every destination named.</summary>
    [Fact]
    public void TheFirstLaunchsWindowOpensTheRail() =>
        Assert.True(RailFold.IsOpen(RailChoice.Automatic, 1000));

    [Fact]
    public void FoldingAWideWindowsRailKeepsItFolded() =>
        Assert.Equal(RailChoice.Collapsed, RailFold.Chosen(open: false, Wide));

    [Fact]
    public void OpeningANarrowWindowsRailKeepsItOpen() =>
        Assert.Equal(RailChoice.Open, RailFold.Chosen(open: true, Narrow));

    /// <summary>Doing with the toggle what the width does anyway is how a reader undoes an earlier choice.</summary>
    [Theory]
    [InlineData(false, Narrow)]
    [InlineData(true, Wide)]
    public void DoingWhatTheWidthWouldHandsTheRailBackToIt(bool open, double width) =>
        Assert.Equal(RailChoice.Automatic, RailFold.Chosen(open, width));

    /// <summary>
    /// Whatever the toggle stores, the rail on screen at that width is what the reader just made it,
    /// so storing a choice never moves the rail under the pointer.
    /// </summary>
    [Theory]
    [InlineData(false, Narrow)]
    [InlineData(true, Narrow)]
    [InlineData(false, Wide)]
    [InlineData(true, Wide)]
    public void AStoredChoiceShowsWhatTheReaderMade(bool open, double width) =>
        Assert.Equal(open, RailFold.IsOpen(RailFold.Chosen(open, width), width));
}
