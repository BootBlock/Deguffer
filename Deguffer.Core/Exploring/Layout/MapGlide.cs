using Deguffer.Core.Viewing;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One move of a zoom from where it is to where it was asked to go, started at
/// <paramref name="Start"/> on whatever clock the caller keeps and played as <paramref name="Motion"/>
/// says, which is <see cref="MotionToken.Camera"/>'s answer for the reader.
///
/// <para>A value started afresh for each turn of the wheel rather than a running animation with
/// state, so a zoom asked for again mid-move begins from wherever the screen is at that moment and
/// never jumps.</para>
/// </summary>
public readonly record struct MapGlide(MapViewport From, MapViewport To, TimeSpan Start, Motion Motion)
{
    /// <summary>Where the move is at <paramref name="now"/>.</summary>
    public MapViewport At(TimeSpan now) => MapViewport.Between(From, To, Motion.At(Start, now));

    /// <summary>Whether the move has arrived by <paramref name="now"/>.</summary>
    public bool IsOverAt(TimeSpan now) => Motion.IsOverAt(Start, now);
}
