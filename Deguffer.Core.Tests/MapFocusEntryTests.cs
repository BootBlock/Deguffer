using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the keyboard comes onto the map's shapes: where it starts, whether an arrow goes on from there,
/// what is picked, and when the menu key acts at all. Where the keyboard was not moving, what a click
/// picked is what the reader chose, and the menu key never acts on anything else (§7.1).
/// </summary>
public sealed class MapFocusEntryTests
{
    private const int Clicked = 7;

    /// <summary>An arrow while the keyboard is moving goes on from where it is, whatever a click picked since.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Clicked)]
    public void AnArrowWhileTheKeyboardMovesGoesOnFromWhereItIs(int? pickedAlone)
    {
        var entry = MapFocusEntry.ByArrow(keyboard: true, pickedAlone);

        Assert.Null(entry.StartsOn);
        Assert.True(entry.Moves);
        Assert.Null(entry.LandsOnlyOn);
    }

    /// <summary>The first arrow after a click starts on what the click picked, and goes on from it.</summary>
    [Fact]
    public void TheFirstArrowAfterAClickGoesOnFromWhatItPicked()
    {
        var entry = MapFocusEntry.ByArrow(keyboard: false, Clicked);

        Assert.Equal(Clicked, entry.StartsOn);
        Assert.True(entry.Moves);
    }

    /// <summary>The first arrow onto a map nothing is picked on marks the shape it starts on, rather than leave it.</summary>
    [Fact]
    public void TheFirstArrowOntoAMapNothingIsPickedOnMarksWhereItStarts()
    {
        var entry = MapFocusEntry.ByArrow(keyboard: false, pickedAlone: null);

        Assert.Null(entry.StartsOn);
        Assert.False(entry.Moves);
        Assert.False(entry.Picks(moved: false));
    }

    /// <summary>An arrow picks the shape it reaches, as a list's arrow keys pick the row, and picks nothing where it stays put.</summary>
    [Fact]
    public void AnArrowPicksOnlyWhereItMoved()
    {
        var entry = MapFocusEntry.ByArrow(keyboard: true, pickedAlone: null);

        Assert.True(entry.Picks(moved: true));
        Assert.False(entry.Picks(moved: false));
    }

    /// <summary>
    /// The map taking the focus puts the keyboard back on the shape it was on, and only where it was on
    /// none on what a click picked. It picks nothing and moves nowhere.
    /// </summary>
    [Theory]
    [InlineData(null, Clicked, Clicked)]
    [InlineData(3, Clicked, null)]
    [InlineData(null, null, null)]
    public void TakingTheFocusStartsWhereTheKeyboardWasOrOnWhatAClickPicked(int? focused, int? pickedAlone, int? startsOn)
    {
        var entry = MapFocusEntry.OnFocus(focused, pickedAlone);

        Assert.Equal(startsOn, entry.StartsOn);
        Assert.False(entry.Moves);
        Assert.False(entry.Picks(moved: false));
        Assert.Null(entry.LandsOnlyOn);
    }

    /// <summary>The menu key while the keyboard moves opens on the shape it is on, whatever that is, and picks it first.</summary>
    [Fact]
    public void TheMenuKeyWhileTheKeyboardMovesActsOnWhereItIs()
    {
        var entry = Assert.NotNull(MapFocusEntry.ByMenu(keyboard: true, Clicked));

        Assert.Null(entry.StartsOn);
        Assert.True(entry.Accepts(Shape(Clicked + 1)));
        Assert.True(entry.Picks(moved: false));
    }

    /// <summary>
    /// The menu key after a click acts on what the click picked: it starts there, and does nothing where
    /// the drawing finds the keyboard on any other shape, because a menu opened there would act on
    /// something the reader never chose (§7.1).
    /// </summary>
    [Fact]
    public void TheMenuKeyAfterAClickActsOnWhatItPickedAndNothingElse()
    {
        var entry = Assert.NotNull(MapFocusEntry.ByMenu(keyboard: false, Clicked));

        Assert.Equal(Clicked, entry.StartsOn);
        Assert.True(entry.Accepts(Shape(Clicked)));
        Assert.False(entry.Accepts(Shape(Clicked + 1)));
        Assert.False(entry.Accepts(Shape(ExploreTile.Aggregated)));
        Assert.True(entry.Picks(moved: false));
    }

    /// <summary>The menu key after a click that picked nothing, or more than one, opens no menu (§7.1).</summary>
    [Fact]
    public void TheMenuKeyAfterAClickThatPickedNothingAloneDoesNothing() =>
        Assert.Null(MapFocusEntry.ByMenu(keyboard: false, pickedAlone: null));

    private static MapShape Shape(int node) => MapShape.Of(node, 1, new MapFrame(0, 0, 0.5, 0.5));
}
