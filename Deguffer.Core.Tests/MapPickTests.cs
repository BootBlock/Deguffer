using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a click, a right-click or the keyboard picks on the map. A pick that names a block is a bulk
/// action §7.1 does not have, and a pick on a picture that answers nothing clears a selection the reader
/// still means.
/// </summary>
public sealed class MapPickTests
{
    private static readonly ExploreHit Folder = new(7, 4096);

    /// <summary>A click on a node picks it.</summary>
    [Fact]
    public void AClickOnANodePicksIt()
    {
        Assert.Equal(new MapPick(7), MapPick.Click(answers: true, pictured: true, Folder));
    }

    /// <summary>Neither block picks anything: a click on one clears the selection, as a click on nothing does.</summary>
    [Fact]
    public void AClickOnABlockPicksNothing()
    {
        foreach (var block in new[] { MapBlock.Aggregate, MapBlock.FreeSpace, MapBlock.Unaccounted, MapBlock.ShadowCopies, MapBlock.ReservedStorage })
        {
            Assert.Equal(new MapPick(null), MapPick.Click(answers: true, pictured: true, new ExploreHit(block.Node, 1 << 20)));
        }
    }

    /// <summary>A click on a picture where nothing is under it clears the selection.</summary>
    [Fact]
    public void AClickOverNothingPicksNothing()
    {
        Assert.Equal(new MapPick(null), MapPick.Click(answers: true, pictured: true, hit: null));
    }

    /// <summary>A click with no picture on screen yet says nothing, so the selection stays as it is.</summary>
    [Fact]
    public void AClickWithNoPictureSaysNothing()
    {
        Assert.Null(MapPick.Click(answers: true, pictured: false, hit: null));
    }

    /// <summary>
    /// A click on a picture that answers nothing, a step of a replay, says nothing, even over a node, so
    /// the selection stays as it is.
    /// </summary>
    [Fact]
    public void AClickOnAPictureThatAnswersNothingSaysNothing()
    {
        Assert.Null(MapPick.Click(answers: false, pictured: true, Folder));
        Assert.Null(MapPick.Click(answers: false, pictured: true, hit: null));
    }

    /// <summary>The keyboard landing on a node picks it, and on a block picks nothing, as a click does.</summary>
    [Fact]
    public void TheKeyboardPicksAsAClickDoes()
    {
        Assert.Equal(new MapPick(7), MapPick.Landing(answers: true, Folder));
        Assert.Equal(new MapPick(null), MapPick.Landing(answers: true, new ExploreHit(MapBlock.Aggregate.Node, 1 << 20)));
        Assert.Null(MapPick.Landing(answers: false, Folder));
    }
}
