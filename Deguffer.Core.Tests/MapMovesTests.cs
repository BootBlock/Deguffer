using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The move the map owes the drawing on its way: only a drawing on its way is owed one, a shift is
/// owed with animation effects off so the camera can follow, and a morph only where its shapes travel.
/// </summary>
public sealed class MapMovesTests
{
    [Theory]
    [InlineData(MapMove.Shift, true)]
    [InlineData(MapMove.Shift, false)]
    [InlineData(MapMove.Morph, true)]
    [InlineData(MapMove.Morph, false)]
    [InlineData(MapMove.None, true)]
    public void NoDrawingOnItsWayIsOwedNothing(MapMove move, bool travels) =>
        Assert.Equal(MapMove.None, MapMoves.Owed(move, travels, onItsWay: false));

    [Fact]
    public void AShiftIsOwedWhetherOrNotItsShapesTravel()
    {
        Assert.Equal(MapMove.Shift, MapMoves.Owed(MapMove.Shift, travels: true, onItsWay: true));
        Assert.Equal(MapMove.Shift, MapMoves.Owed(MapMove.Shift, travels: false, onItsWay: true));
    }

    [Fact]
    public void AMorphIsOwedOnlyWhereItsShapesTravel()
    {
        Assert.Equal(MapMove.Morph, MapMoves.Owed(MapMove.Morph, travels: true, onItsWay: true));
        Assert.Equal(MapMove.None, MapMoves.Owed(MapMove.Morph, travels: false, onItsWay: true));
    }

    [Fact]
    public void ARedrawForAnyOtherReasonOwesNothing() =>
        Assert.Equal(MapMove.None, MapMoves.Owed(MapMove.None, travels: true, onItsWay: true));
}
