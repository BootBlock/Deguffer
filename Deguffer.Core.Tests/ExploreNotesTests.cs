using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>The four notes on the Explore page's card, and the button that brings them back.</summary>
public sealed class ExploreNotesTests
{
    /// <summary>Each of the four counts as something to say, on its own.</summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void EachNoteCounts(bool selection, bool stale, bool view, bool route)
    {
        Assert.True(new ExploreNotes(selection, stale, view, route, Dismissed: false).HasAny);
    }

    /// <summary>
    /// The notes and the button that brings them back take turns, and both are off while there is
    /// nothing to read, which is what makes the button itself the signal.
    /// </summary>
    [Fact]
    public void ThePanelAndTheButtonTakeTurnsAndBothGoWithNothingToSay()
    {
        var nothing = new ExploreNotes(Selection: false, Stale: false, View: false, Route: false, Dismissed: false);
        var route = nothing with { Route = true };

        Assert.False(nothing.ShowsPanel);
        Assert.False(nothing.ShowsButton);
        Assert.False((nothing with { Dismissed = true }).ShowsButton);

        Assert.True(route.ShowsPanel);
        Assert.False(route.ShowsButton);
        Assert.False((route with { Dismissed = true }).ShowsPanel);
        Assert.True((route with { Dismissed = true }).ShowsButton);
    }

    /// <summary>
    /// Only the stale note is a warning: after a removal it is the whole of §7.1's statement that the
    /// sizes are lower bounds.
    /// </summary>
    [Fact]
    public void OnlyTheStaleNoteWarns()
    {
        var others = new ExploreNotes(Selection: true, Stale: false, View: true, Route: true, Dismissed: true);

        Assert.False(others.Warns);
        Assert.True((others with { Stale = true }).Warns);
    }
}
