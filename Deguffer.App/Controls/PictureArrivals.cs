using Deguffer.App.Diagnostics;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Controls;

/// <summary>
/// How long each new picture handed to an <see cref="ExploreMap"/> waits for its first drawing to
/// reach the screen, as <see cref="ShellEvents.PictureStart"/> and <see cref="ShellEvents.PictureStop"/>
/// state it: from the hand-off to the first frame composed after a drawing of it became the one the map
/// works from. The span holds the layout, the paint or the shade (the first shader compile included),
/// and the copy to the screen.
///
/// <para>It waits on no frame while no trace listens, so a session with no trace pays a check of
/// <see cref="System.Diagnostics.Tracing.EventSource.IsEnabled()"/> for each picture and nothing
/// else.</para>
/// </summary>
internal sealed class PictureArrivals(IFrameClock clock)
{
    /// <summary>The number of the picture waiting for its first drawing, or null where none is.</summary>
    private int? _waiting;

    /// <summary>
    /// The map was handed <paramref name="asked"/>, its <paramref name="number"/>th new picture. One
    /// still waiting for a drawing will never have one now. A picture of no tree has nothing to draw,
    /// so it is not timed.
    /// </summary>
    public void Handed(int number, MapPicture asked)
    {
        if (_waiting is { } earlier)
        {
            ShellEvents.Log.PictureStop(earlier, PictureOutcome.Superseded);
            _waiting = null;
        }

        if (asked.Tree is null || !ShellEvents.Log.IsEnabled())
        {
            return;
        }

        ShellEvents.Log.PictureStart(number, asked.View);
        _waiting = number;
    }

    /// <summary>
    /// A drawing became the one the map works from. The first since a picture was handed over is on
    /// screen at the next frame the window composes, which is when the wait is said to end.
    /// </summary>
    public void Presented()
    {
        if (_waiting is not { } picture)
        {
            return;
        }

        _waiting = null;

        NextFrame.Then(clock, () => ShellEvents.Log.PictureStop(picture, PictureOutcome.Shown));
    }
}
