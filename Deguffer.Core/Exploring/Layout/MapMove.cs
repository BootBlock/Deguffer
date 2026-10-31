namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How the map moves from the drawing on screen to the next one as it arrives, rather than replacing
/// it a region at a time. See <see cref="MapMoves.Owed"/>.
/// </summary>
public enum MapMove
{
    /// <summary>The next drawing replaces the one on screen.</summary>
    None,

    /// <summary>
    /// The same place laid out again, a scan's next snapshot or a new spacing: the large shapes
    /// travel to where they are now and the rest crossfades (<see cref="ShapeTravel"/>).
    /// </summary>
    Shift,

    /// <summary>
    /// The same place in another view: the largest shapes travel from where the one view draws them
    /// to where the other does, and the rest crossfades (<see cref="ViewMorph"/>).
    /// </summary>
    Morph,
}
