using System.Diagnostics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Controls;

/// <summary>
/// The clock behind a folder opening on the map: where the old picture and the new one are at each
/// frame of the move from one to the other.
///
/// <para>The same split as <see cref="ExploreZoom"/>: the arithmetic is Core's, in
/// <see cref="MapDescent"/>, and what is left here is the frame clock, which needs a window.</para>
/// </summary>
internal sealed class ExploreDescent
{
    /// <summary>One clock for the life of the map, read at each frame rather than started per move (G5).</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private MapDescent? _move;

    /// <summary>Raised at each frame of the move, and once more at its end.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised once when the move is over, whether it ran its course or was finished early.</summary>
    public event EventHandler? Arrived;

    public bool IsMoving => _move is not null;

    /// <summary>Where the new drawing is on the screen now. See <see cref="MapDescent.Opened"/>.</summary>
    public MapFrame Opened { get; private set; } = MapFrame.Whole;

    /// <summary>Where the old picture's screen is now. See <see cref="MapDescent.Departing"/>.</summary>
    public MapFrame Departing { get; private set; } = MapFrame.Whole;

    /// <summary>How opaque the new drawing is now.</summary>
    public double Opacity { get; private set; } = 1;

    /// <summary>Open the shape that was at <paramref name="shape"/> on the screen, in fractions of it.</summary>
    public void Start(MapFrame shape)
    {
        if (_move is null)
        {
            CompositionTarget.Rendering += OnRendering;
        }

        _move = new MapDescent(shape, _clock.Elapsed);

        Follow(_move.Value, _clock.Elapsed);
    }

    /// <summary>
    /// End the move where it was going. For anything that needs the screen settled first: another
    /// drawing, a wheel turn, a drag, or the map leaving the screen.
    /// </summary>
    public void Finish()
    {
        if (_move is not { } move)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _move = null;

        Follow(move, TimeSpan.MaxValue);
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One frame of the move. Subscribed only while there is one, because the event fires at every
    /// frame the window composes.
    /// </summary>
    private void OnRendering(object? sender, object e)
    {
        if (_move is not { } move)
        {
            return;
        }

        var now = _clock.Elapsed;

        if (move.IsOverAt(now))
        {
            Finish();
            return;
        }

        Follow(move, now);
    }

    private void Follow(MapDescent move, TimeSpan now)
    {
        Opened = move.Opened(now);
        Departing = move.Departing(now);
        Opacity = move.Opacity(now);

        Moved?.Invoke(this, EventArgs.Empty);
    }
}
