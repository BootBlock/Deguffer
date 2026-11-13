using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The names' sheet hands out rectangles that never overlap and keep a clear pixel between them, packs
/// names of one height into one row, takes back the room a name leaves, and says when it is full.
/// </summary>
public sealed class SheetShelvesTests
{
    [Fact]
    public void RectanglesNeverOverlapAndKeepAPixelClearBetweenThem()
    {
        var shelves = new SheetShelves(256, 256);
        var taken = new List<SheetRegion>();

        for (var i = 0; i < 60; i++)
        {
            taken.Add(shelves.Take(17 + (i % 23), 11 + (13 * (i % 3)))!.Value);
        }

        for (var a = 0; a < taken.Count; a++)
        {
            Assert.InRange(taken[a].X + taken[a].Width, 1, 255);
            Assert.InRange(taken[a].Y + taken[a].Height, 1, 255);

            for (var b = a + 1; b < taken.Count; b++)
            {
                Assert.False(Touch(taken[a], taken[b]), $"{taken[a]} and {taken[b]} share a pixel or its gutter.");
            }
        }
    }

    /// <summary>Names of one height go side by side in one row, rather than a row each.</summary>
    [Fact]
    public void NamesOfOneHeightShareARow()
    {
        var shelves = new SheetShelves(256, 256);

        var first = shelves.Take(50, 14)!.Value;
        var taller = shelves.Take(50, 30)!.Value;
        var second = shelves.Take(50, 14)!.Value;

        Assert.Equal(first.Y, second.Y);
        Assert.Equal(first.X + 51, second.X);
        Assert.NotEqual(first.Y, taller.Y);
    }

    /// <summary>
    /// The room a name gives back joins the room beside it, so a wider name takes it, and a sheet with
    /// no room left says so rather than handing out a rectangle another name holds.
    /// </summary>
    [Fact]
    public void RoomGivenBackJoinsTheRoomBesideIt()
    {
        var shelves = new SheetShelves(101, 15);

        var left = shelves.Take(49, 14)!.Value;
        var right = shelves.Take(49, 14)!.Value;

        Assert.Null(shelves.Take(10, 14));

        shelves.Give(left);
        shelves.Give(right);

        var whole = shelves.Take(100, 14);

        Assert.Equal(new SheetRegion(0, 0, 100, 14), whole);
    }

    /// <summary>
    /// A row emptied at the bottom of the sheet goes back to the sheet, so a row of another height can
    /// take its place, and an emptied sheet has all its room again.
    /// </summary>
    [Fact]
    public void AnEmptiedRowOrSheetHasItsRoomAgain()
    {
        var shelves = new SheetShelves(100, 31);

        var small = shelves.Take(50, 14)!.Value;
        shelves.Take(50, 14);

        Assert.Null(shelves.Take(50, 20));

        shelves.Clear();

        Assert.NotNull(shelves.Take(99, 30));
        Assert.Null(shelves.Take(1, 1));

        shelves.Clear();
        small = shelves.Take(50, 14)!.Value;
        shelves.Give(small);

        Assert.Equal(new SheetRegion(0, 0, 99, 29), shelves.Take(99, 29));
    }

    /// <summary>A shorter name goes in a taller row when there is no room for a row of its own, but not in one more than twice its height.</summary>
    [Fact]
    public void AShortNameUsesATallerRowOnlyWhenItMust()
    {
        var shelves = new SheetShelves(100, 26);

        var tall = shelves.Take(30, 24)!.Value;
        var shortName = shelves.Take(30, 14);

        Assert.Equal(tall.Y, shortName!.Value.Y);
        Assert.Null(shelves.Take(30, 5));
    }

    [Fact]
    public void ARectangleLargerThanTheSheetIsRefused()
    {
        var shelves = new SheetShelves(100, 100);

        Assert.Null(shelves.Take(100, 10));
        Assert.Null(shelves.Take(10, 100));
        Assert.Null(shelves.Take(0, 10));
        Assert.Equal(99, shelves.Width);
    }

    /// <summary>Whether two rectangles, each with its pixel of gutter to the right and below, share any pixel.</summary>
    private static bool Touch(SheetRegion a, SheetRegion b) =>
        a.X < b.X + b.Width + 1 && b.X < a.X + a.Width + 1 && a.Y < b.Y + b.Height + 1 && b.Y < a.Y + a.Height + 1;
}
