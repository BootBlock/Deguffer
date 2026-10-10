using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Input;

namespace Deguffer.App.Controls;

/// <summary>
/// What the compositor takes from a hand on the map: the touchpad, touch, and the wheel with Ctrl
/// held, handed to the map's interaction tracker (see <see cref="ExploreZoom"/>), which moves the
/// camera with nothing on the UI thread.
///
/// <para>Apart from the tracker's owner because it only says which inputs go where, and the owner is
/// the one party that asks the tracker for moves and hears its reports.</para>
/// </summary>
internal sealed class MapInteraction
{
    private readonly VisualInteractionSource _source;

    private bool _movable;

    private bool _elastic;

    /// <summary>
    /// Hand inputs on <paramref name="source"/> to <paramref name="tracker"/>. The map's own visual,
    /// which does not move, takes the hand: what the compositor hit-tests is the map's bounds rather
    /// than the picture moving inside them.
    /// </summary>
    public MapInteraction(Visual source, InteractionTracker tracker)
    {
        _source = VisualInteractionSource.Create(source);
        _source.IsPositionXRailsEnabled = false;
        _source.IsPositionYRailsEnabled = false;
        tracker.InteractionSources.Add(_source);

        Configure();
    }

    /// <summary>
    /// Whether the camera can be moved: on a map the page lets zoom, showing a drawing that zooms.
    /// Elsewhere the touchpad, touch and the wheel go to whatever else would have them, such as the
    /// page's scroller.
    /// </summary>
    public bool Movable
    {
        get => _movable;
        set
        {
            if (_movable == value)
            {
                return;
            }

            _movable = value;
            Configure();
        }
    }

    /// <summary>Whether a hand let go coasts on, which is whether the reader has animation effects on.</summary>
    public bool Elastic
    {
        get => _elastic;
        set
        {
            if (_elastic == value)
            {
                return;
            }

            _elastic = value;
            Configure();
        }
    }

    /// <summary>
    /// Hand a touch on the map to the compositor, which pans, pinches and flicks the picture from here
    /// on. A tap is left alone: the compositor takes the touch only once it starts to move.
    /// </summary>
    public void Redirect(PointerPoint point)
    {
        if (!_movable)
        {
            return;
        }

        try
        {
            _source.TryRedirectForManipulation(point);
        }
        catch (COMException e) when (e.HResult == unchecked((int)0x80070005))
        {
            // E_ACCESSDENIED: the system refuses a touch it has already begun to treat as something
            // else, which happens. The touch stays with the map, which treats it as a tap or a drag.
        }
    }

    /// <summary>
    /// Give the compositor the inputs it can take, or none where the camera cannot move. Inertia is on
    /// only where the reader has animation effects on.
    /// </summary>
    private void Configure()
    {
        var mode = !_movable
            ? InteractionSourceMode.Disabled
            : _elastic ? InteractionSourceMode.EnabledWithInertia : InteractionSourceMode.EnabledWithoutInertia;

        _source.PositionXSourceMode = mode;
        _source.PositionYSourceMode = mode;
        _source.ScaleSourceMode = mode;

        // The wheel's own position modes stay off, because a map has no up and down to scroll
        // through. Its scale mode is on, but only Ctrl and the wheel reach the compositor: a plain
        // wheel still comes to the map as a pointer event, and the map glides for it.
        _source.PointerWheelConfig.PositionXSourceMode = InteractionSourceRedirectionMode.Disabled;
        _source.PointerWheelConfig.PositionYSourceMode = InteractionSourceRedirectionMode.Disabled;
        _source.PointerWheelConfig.ScaleSourceMode = InteractionSourceRedirectionMode.Enabled;

        _source.ManipulationRedirectionMode = _movable
            ? VisualInteractionSourceRedirectionMode.CapableTouchpadAndPointerWheel
            : VisualInteractionSourceRedirectionMode.Off;
    }
}
