using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>
/// The entrance a page plays as the navigation rail brings it in: its header rises a short way into
/// place as it fades in, and the rest of the page follows a step behind (<see cref="PageEntrance"/>).
///
/// <para>Each part comes in as an <see cref="ElementEntrance"/>. A page back from the navigation cache
/// keeps its state and its scroll position, and plays only this.</para>
///
/// <para>Every page lays itself out as one panel whose first row, or first child, is its header, so
/// that is what is brought in first. A page laid out otherwise comes in whole, at the header's time.</para>
/// </summary>
internal sealed class PageArrival(IMotionPolicy motion)
{
    /// <summary>How far below its place a part of the page starts, in device-independent pixels.</summary>
    private readonly ElementEntrance _entrance = new(rise: 16);

    /// <summary>The entrance waiting for its page's first frame, if one is.</summary>
    private EventHandler<object>? _waiting;

    /// <summary>Bring <paramref name="page"/> in.</summary>
    public void Play(Page page)
    {
        // A page left before it was ever drawn is not brought in after all.
        if (_waiting is not null)
        {
            CompositionTarget.Rendering -= _waiting;
            _waiting = null;
        }

        var played = motion.For(MotionToken.Page);

        if (played.IsInstant)
        {
            return;
        }

        var parts = Parts(page, played);

        foreach (var (part, _) in parts)
        {
            _entrance.Hold(part, played);
        }

        // Started on the first frame after the page is in the window rather than now, because a page
        // can take a few hundred milliseconds to build and lay out, and the entrance is shorter than
        // that. Until then each part is held where the entrance starts, unseen.
        _waiting = (_, _) =>
        {
            if (!page.IsLoaded)
            {
                return;
            }

            CompositionTarget.Rendering -= _waiting;
            _waiting = null;

            foreach (var (part, delay) in parts)
            {
                _entrance.Enter(part, played, delay);
            }
        };

        CompositionTarget.Rendering += _waiting;
    }

    /// <summary>Each part of <paramref name="page"/> to bring in, and how far behind the header it starts.</summary>
    private static List<(UIElement Part, TimeSpan Delay)> Parts(Page page, Motion played)
    {
        if (Panel(page) is not { } panel)
        {
            return [(page, PageEntrance.Start(isHeader: true, played))];
        }

        var parts = new List<(UIElement, TimeSpan)>(panel.Children.Count);

        for (var i = 0; i < panel.Children.Count; i++)
        {
            var part = panel.Children[i];
            var isHeader = panel is Grid ? Grid.GetRow((FrameworkElement)part) == 0 : i == 0;

            parts.Add((part, PageEntrance.Start(isHeader, played)));
        }

        return parts;
    }

    /// <summary>
    /// The panel a page is laid out in: its content, or the content of the scroller its content is.
    /// </summary>
    private static Panel? Panel(Page page) => page.Content switch
    {
        Panel panel => panel,
        ScrollViewer { Content: Panel panel } => panel,
        _ => null,
    };
}
