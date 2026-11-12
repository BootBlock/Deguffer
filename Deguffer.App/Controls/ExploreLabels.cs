using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The names laid over the map, as real text rather than as pixels in the picture.
///
/// <para>Text controls, so they scale with the user's text size and a screen reader could reach
/// them — neither of which a name burnt into the picture offers. A shape too small to read is a
/// shape the drawing gives no label, so there are only as many as the picture has room to write.</para>
///
/// <para>Each name is placed by the rule its drawing placed it by (<see cref="ExploreLabel.Across"/> and
/// <see cref="ExploreLabel.Down"/>), and the compositor works that rule out again on every frame as
/// the map's camera moves and as a folder opening carries the picture, so the names stay on the map
/// through every zoom, drag and folder opened with no work on the UI thread per frame. A zoom ends
/// with each name already where the drawing at the new zoom puts it, rather than jumping there.
/// Only the anchor moves: the text stays upright and at the reader's size, so it fades out as its
/// shape shrinks round it, before it reaches a shape that is not its own
/// (<see cref="ExploreLabel.Fade"/>), and a shape that grows keeps its name.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting pieces of
/// text where somebody else said they go (G1). Each piece is a <see cref="MapName"/>.</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    private readonly Compositor _compositor;

    private readonly MapCamera _camera;

    /// <summary>The map's device, which each name in SpaceMonger's lettering is drawn through.</summary>
    private readonly CompositionDevice _device;

    private readonly IMotionPolicy _motion;

    /// <summary>
    /// Where the drawing the names were laid out for lies in the picture, per device-independent
    /// pixel of that drawing: a <c>Scale</c> and an <c>Offset</c>, combined with the camera's by the
    /// compositor.
    /// </summary>
    private readonly CompositionPropertySet _placed;

    /// <summary>
    /// Where one device-independent pixel of that drawing is on the screen: <see cref="_placed"/> and
    /// the camera combined, as a <c>Scale</c> and an <c>Offset</c>, by expressions of its own. Worked
    /// out once here for every name, because the compositor refuses an expression as long as each
    /// name's would be with this written into it four times.
    /// </summary>
    private readonly CompositionPropertySet _screen;

    private static readonly Func<ExploreLabel, int> NodeOf = static label => label.Node;

    /// <summary>The type SpaceMonger's lettering is set in, shared by every name on the map.</summary>
    private readonly ClassicFormats _formats = new();

    /// <summary>Every name made, and which of them writes each name of the drawing shown.</summary>
    private readonly NameSlots<MapName> _names;

    private ISizedTree? _tree;

    /// <summary>What a folder opening carries the picture by: the visual over the drawings the names follow.</summary>
    private Visual _carried;

    /// <summary>
    /// Names that move with <paramref name="camera"/>, wherever <see cref="Place"/> puts them in the
    /// picture, and with <paramref name="carried"/> as a folder opening carries the picture. A name in
    /// SpaceMonger's lettering is drawn through <paramref name="device"/>, the map's own.
    /// </summary>
    public ExploreLabels(MapCamera camera, Visual carried, CompositionDevice device, IMotionPolicy motion)
    {
        // Never the thing being clicked. A click that landed on a name rather than the shape under
        // it would select whatever that name happened to overlap.
        IsHitTestVisible = false;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _camera = camera;
        _carried = carried;
        _device = device;
        _motion = motion;

        // A name in SpaceMonger's lettering is drawn into an image, whose contents a device lost or
        // replaced takes with it. The map's own device replaced is followed by the map's redraw, which
        // writes each name again and so draws it on the new device.
        Loaded += (_, _) => CompositionTarget.SurfaceContentsLost += OnContentsLost;
        Unloaded += (_, _) => CompositionTarget.SurfaceContentsLost -= OnContentsLost;

        _placed = _compositor.CreatePropertySet();
        _placed.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _placed.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        _placed.InsertVector2(MapName.Extent, Vector2.Zero);
        _placed.InsertScalar(MapName.Pixel, 1);

        _names = new NameSlots<MapName>(Make);

        _screen = _compositor.CreatePropertySet();
        _screen.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _screen.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        Combine(nameof(Visual.Scale), "Vector3(placed.Scale.X * camera.Scale.X, placed.Scale.Y * camera.Scale.Y, 1)");
        Combine(
            nameof(Visual.Offset),
            "Vector3((placed.Offset.X * camera.Scale.X) + camera.Offset.X, (placed.Offset.Y * camera.Scale.Y) + camera.Offset.Y, 0)");
    }

    /// <summary>A name the drawings so far have not needed, on the map.</summary>
    private MapName Make()
    {
        var name = new MapName(_compositor, _device, _formats, _camera.Properties, _placed, _screen, _carried);

        Children.Add(name.Text.Element);

        return name;
    }

    private void Combine(string property, string expression)
    {
        var combined = _compositor.CreateExpressionAnimation(expression);
        combined.SetReferenceParameter("placed", _placed);
        combined.SetReferenceParameter("camera", _camera.Properties);
        _screen.StartAnimation(property, combined);
    }

    /// <summary>
    /// Put the names where <paramref name="placed"/> says one device-independent pixel of the drawing
    /// they name lies in the picture, on a canvas <paramref name="width"/> by <paramref name="height"/>
    /// device-independent pixels.
    /// </summary>
    public void Place(MapTransform placed, double width, double height)
    {
        _placed.Place(placed);
        _placed.InsertVector2(MapName.Extent, new Vector2((float)width, (float)height));
    }

    /// <summary>
    /// Follow <paramref name="carried"/> as a folder opening carries the picture, for the set of
    /// drawings the names are of now. The map swaps its two sets as a folder opens.
    /// </summary>
    public void Ride(Visual carried)
    {
        if (carried == _carried)
        {
            return;
        }

        _carried = carried;

        foreach (var name in _names.All)
        {
            name.Follow(carried);
        }
    }

    /// <summary>
    /// Put <paramref name="caption"/>'s text on each shape <paramref name="drawing"/> chose to label,
    /// at <paramref name="scale"/> canvas pixels to the device-independent pixel.
    ///
    /// <para>The caption comes from the page rather than from here, because only it knows what the
    /// tree's nodes are: a drive's are a file name and a size, and a memory picture's are a process or
    /// a part of Windows. How it is written is the drawing's lettering (<see cref="MapCaption.Text"/>).</para>
    ///
    /// <para>The names are kept and written over rather than rebuilt, and a drawing with fewer puts
    /// the rest away. A name of the tree shown before is written by the control that wrote it, so
    /// its text and its measurement are those it has (<see cref="NameSlots{TSlot}"/>). A scan
    /// repaints this several times a second, and each rebuild would throw away controls and their
    /// brushes and make the framework measure and arrange a fresh set of them, for text that has
    /// usually not changed (G5).</para>
    ///
    /// <para>A name the map was not showing fades in, where the drawing is of the tree it was showing:
    /// detail a zoom or a folder opened uncovered. Another tree is a scan's next snapshot or another
    /// picture altogether, whose nodes are not the same nodes, and its names are put up at once, as a
    /// scan repainting several times a second needs.</para>
    /// </summary>
    public void Show(ExploreSurface drawing, double scale, Func<ExploreLabel, MapCaption> caption)
    {
        Visibility = Visibility.Visible;
        _placed.InsertScalar(MapName.Pixel, (float)scale);

        var sameTree = ReferenceEquals(drawing.Tree, _tree);
        var arrival = _motion.For(MotionToken.Detail);

        _names.Assign(drawing.Labels, NodeOf, sameTree);

        foreach (var spare in _names.Surplus)
        {
            spare.Collapse();
        }

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var name = _names.Assigned[i];
            name.Show();

            var measured = name.Text.Write(
                caption(label).Text(drawing.Lettering, label.Lines),
                drawing.Lettering,
                label.Width / scale,
                label.Centred,
                label.Colour.ToColor(),
                label.Rotation,
                drawing.LineSpacing / scale,
                scale);
            name.Place(label, measured, scale);

            if (!sameTree)
            {
                name.Arrive();
            }
            else if (_names.Histories[i] == NameHistory.Unnamed)
            {
                name.Enter(arrival, _compositor.MotionEase());
            }
            else if (_names.Histories[i] == NameHistory.NamedElsewhere)
            {
                name.Arrive();
            }

            // Otherwise still arriving where it was already this node's name.
        }

        _tree = drawing.Tree;
    }

    private void OnContentsLost(object? sender, object e)
    {
        // The names put away too, which a drawing shows again without drawing them again where what
        // they show is unchanged.
        foreach (var name in _names.All)
        {
            name.Text.Redraw();
        }
    }

    /// <summary>
    /// Take the names off until the layout that places them arrives.
    ///
    /// <para>For while a resize settles, a folder opens out of the picture they name, or the device
    /// was replaced. A resize stretches the picture on each axis apart, and a name the layout gave room
    /// to at one size can run over its neighbours at another.</para>
    /// </summary>
    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>
    /// Put them back without redrawing them, for a caller that has dropped the redraw which would
    /// have. The positions are still the ones the last drawing gave, because a size that was never
    /// drawn never moved them.
    /// </summary>
    public void Reveal() => Visibility = Visibility.Visible;

    /// <summary>Take them off for good, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        Children.Clear();
        _names.Clear();
        _tree = null;
    }
}
