using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The row a gesture in a list landed on. A button inside a row takes the gesture as its own, which is
/// what kept one Enter on a Memory row's chevron from opening the row twice.
/// </summary>
public sealed class RowHitTests
{
    [Fact]
    public void AGestureOnARowsTextLandsOnTheRow()
    {
        var row = new Element(RowPart.Row);

        Assert.Same(row, Find(new Element(RowPart.Content), new Element(RowPart.Content), row, new Element(RowPart.Content)));
    }

    [Fact]
    public void AGestureOnTheRowItselfLandsOnIt()
    {
        var row = new Element(RowPart.Row);

        Assert.Same(row, Find(row));
    }

    /// <summary>
    /// The chevron on a Memory row: the button turns Enter into a click that opens the row, and the
    /// row's own key handler must not open it a second time.
    /// </summary>
    [Fact]
    public void AGestureOnAButtonInsideARowLandsOnNoRow() =>
        Assert.Null(Find(new Element(RowPart.Content), new Element(RowPart.Button), new Element(RowPart.Row)));

    [Fact]
    public void AGestureBesideTheRowsLandsOnNoRow() =>
        Assert.Null(Find(new Element(RowPart.Content), new Element(RowPart.Content)));

    /// <summary>The nearest row is the one, where a row holds rows as a tree's does.</summary>
    [Fact]
    public void TheNearestRowIsTheOne()
    {
        var inner = new Element(RowPart.Row);

        Assert.Same(inner, Find(new Element(RowPart.Content), inner, new Element(RowPart.Row)));
    }

    /// <summary>Lazy, so a walk up a deep tree stops at the row.</summary>
    [Fact]
    public void TheWalkStopsAtTheRow()
    {
        var walked = 0;

        IEnumerable<Element> Walk()
        {
            walked++;
            yield return new Element(RowPart.Row);
            walked++;
            yield return new Element(RowPart.Content);
        }

        RowHit.Find(Walk(), element => element.Part);

        Assert.Equal(1, walked);
    }

    private static Element? Find(params Element[] fromSource) => RowHit.Find(fromSource, element => element.Part);

    private sealed record Element(RowPart Part);
}
