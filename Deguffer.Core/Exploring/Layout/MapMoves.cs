namespace Deguffer.Core.Exploring.Layout;

/// <summary>The move the map owes the drawing on its way, from the move a change asked for.</summary>
public static class MapMoves
{
    /// <summary>
    /// The move owed when <paramref name="move"/> is asked for, over a motion that
    /// <paramref name="travels"/> or not, with a drawing <paramref name="onItsWay"/> or not.
    ///
    /// <para>Owed only to a drawing on its way. A map that cannot be seen, or has nothing to draw,
    /// draws nothing, and a drawing kept from before has already been shown.</para>
    ///
    /// <para>A shift is owed with animation effects off too: its shapes do not travel, but the camera
    /// still follows what the reader zoomed into (<see cref="MapFollow"/>), straight to where it is
    /// now. A morph is owed only where its shapes travel. A swap at once lands the new view in sight
    /// a region at a time, and what has landed answers the pointer as it would for any other picture
    /// (§7.1).</para>
    /// </summary>
    public static MapMove Owed(MapMove move, bool travels, bool onItsWay) => move switch
    {
        _ when !onItsWay => MapMove.None,
        MapMove.Morph when !travels => MapMove.None,
        _ => move,
    };
}
