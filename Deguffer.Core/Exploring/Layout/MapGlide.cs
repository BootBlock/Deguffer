using Deguffer.Core.Viewing;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One move of a map's camera from <paramref name="From"/> to <paramref name="To"/> that changes the
/// zoom, as the compositor plays it: a scale about the still point <see cref="MapTracking.Pivot"/>
/// finds, through the key frames <see cref="Zooms"/> gives.
///
/// <para>Key frames rather than one eased step from end to end, because the zoom moves by equal
/// ratios (see <see cref="MapViewport.Between"/>) and the compositor eases a value by equal steps. A
/// zoom from 1 to 8 eased by steps spends most of its time on the last doubling, which reads as a
/// lurch. Enough frames, each a straight step, follow the curve closely enough not to show.</para>
/// </summary>
public readonly record struct MapGlide(MapViewport From, MapViewport To)
{
    /// <summary>How many straight steps the move is played in.</summary>
    public const int Steps = 12;

    /// <summary>
    /// The zoom at each of <see cref="Steps"/> + 1 evenly spaced moments of the move, as a fraction of
    /// its time from 0 to 1, eased as every camera move is (<see cref="Motion.Ease"/>).
    /// </summary>
    public IReadOnlyList<(double Time, double Zoom)> Zooms()
    {
        var frames = new (double Time, double Zoom)[Steps + 1];

        for (var step = 0; step <= Steps; step++)
        {
            var time = step / (double)Steps;

            frames[step] = (time, MapViewport.Between(From, To, Motion.Ease(time)).Zoom);
        }

        return frames;
    }
}
