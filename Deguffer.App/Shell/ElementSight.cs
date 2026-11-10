using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Shell;

/// <summary>
/// Whether anyone can see an element (<see cref="Sights"/>), and a call back to its owner whenever that
/// may have changed: the window minimised, restored, hidden or shown, the element resized, or any window
/// on the desktop moving over it or away (<see cref="WindowCover"/>).
///
/// <para>Followed only between <see cref="Follow"/> and <see cref="Stop"/>, which the owner calls as the
/// element is loaded and unloaded, because the desktop's window events are hooked for as long as anyone
/// follows them.</para>
/// </summary>
internal sealed class ElementSight
{
    /// <summary>
    /// How long after a window moves the answer is asked again. A window dragged across the element
    /// raises a move for every frame of the drag, and asking at most ten times a second keeps the asking
    /// far cheaper than the work it saves, while work uncovered resumes before the reader can look for it.
    /// </summary>
    private static readonly TimeSpan Resurvey = TimeSpan.FromMilliseconds(100);

    private readonly FrameworkElement _element;
    private readonly Action _changed;
    private readonly Throttle _resurvey;
    private Subscriptions? _following;

    /// <param name="element">The element whose sight is followed.</param>
    /// <param name="changed">What runs, on the element's thread, when the answer may have changed.</param>
    public ElementSight(FrameworkElement element, Action changed)
    {
        _element = element;
        _changed = changed;
        _resurvey = new Throttle(element.DispatcherQueue, Resurvey, changed);
    }

    /// <summary>Whether anyone can see the element now.</summary>
    public Sight Now => Sights.Of(_element.IsLoaded, _element.XamlRoot?.IsHostVisible, IsCovered);

    /// <summary>Start following, or start again in the element's current window.</summary>
    public void Follow()
    {
        _following?.Dispose();
        _following = new Subscriptions()
            .Add(() => WindowStackEvents.Current.Changed += OnStackChanged, () => WindowStackEvents.Current.Changed -= OnStackChanged)
            .Add(() => _element.SizeChanged += OnSizeChanged, () => _element.SizeChanged -= OnSizeChanged);

        if (_element.XamlRoot is { } root)
        {
            _following.Add(() => root.Changed += OnRootChanged, () => root.Changed -= OnRootChanged);
        }
    }

    /// <summary>Stop following, and call off any answer on its way.</summary>
    public void Stop()
    {
        _following?.Dispose();
        _following = null;
        _resurvey.Stop();
    }

    /// <summary>
    /// Not where Windows will not say where the element is, so a doubt leaves it seen.
    /// </summary>
    private bool IsCovered() => ScreenPlace.Of(_element) is { } place && WindowCover.Hides(place.Window, place.Rect);

    /// <summary>
    /// The window was minimised, restored, hidden or shown, or moved between displays. Told at once
    /// rather than throttled, so a restored window shows its work moving as it appears.
    /// </summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => _changed();

    /// <summary>A window on the desktop moved, came to the front, or was shown, hidden, minimised or cloaked.</summary>
    private void OnStackChanged(object? sender, EventArgs e) => _resurvey.Poke();

    /// <summary>The element grew or shrank inside its window, which can uncover part of it.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => _resurvey.Poke();
}
