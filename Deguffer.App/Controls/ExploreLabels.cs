using Deguffer.App.Shell;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Controls;

/// <summary>
/// The names laid over the map, drawn apart from the picture rather than burnt into it.
///
/// <para>Apart, so they keep the reader's text size while the picture is magnified, and fade as their
/// shapes shrink round them. A shape too small to read is a shape the drawing gives no label, so there
/// are only as many as the picture has room to write.</para>
///
/// <para>Each name is a rectangle of one surface (<see cref="NameSheet"/>), shown by a visual of its
/// own (<see cref="NameSprite"/>) hung under one visual that the compositor carries with the map's
/// camera and with a folder opening (<see cref="NameLayer"/>), and faded by its step
/// (<see cref="NameFades"/>). So a pan, a zoom or a fling costs the compositor the same few expressions
/// whatever the number of names, and the UI thread nothing per frame. The text stays upright and at
/// the reader's size while its anchor moves with the picture, so it fades out as its shape shrinks
/// round it, before it reaches a shape that is not its own (<see cref="ExploreLabel.Fade"/>), and a
/// shape that grows keeps its name.</para>
///
/// <para>While the picture moves, each name keeps the distance from its point of the picture that its
/// drawing put it at (<see cref="LabelAxis.Carried"/>). A name its drawing kept at the edge of the
/// canvas, for a shape running off it, goes with its shape, and the drawing made where the picture
/// stops puts it back at the edge.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting pieces of
/// text where somebody else said they go (G1).</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    private static readonly Func<ExploreLabel, int> NodeOf = static label => label.Node;

    private readonly Compositor _compositor;

    /// <summary>The map's device, which every name is drawn through.</summary>
    private readonly CompositionDevice _device;

    private readonly IMotionPolicy _motion;

    private readonly NameLayer _layer;

    /// <summary>The one surface every name is drawn into.</summary>
    private readonly NameSheet _sheet;

    /// <summary>The type the names are set in, shared by every name on the map.</summary>
    private readonly NameFormats _formats = new();

    /// <summary>Every name made, and which of them writes each name of the drawing shown.</summary>
    private readonly NameSlots<NameSprite> _names;

    /// <summary>What <see cref="Write"/> writes, for as long as the device's guard runs it.</summary>
    private (ExploreSurface Drawing, double Scale, Func<ExploreLabel, MapCaption> Caption) _writing;

    private ISizedTree? _tree;

    /// <summary>
    /// Names that move with <paramref name="camera"/>, wherever <see cref="Place"/> puts them in the
    /// picture, and with <paramref name="carried"/> as a folder opening carries the picture, drawn
    /// through <paramref name="graphics"/>, the map's own.
    /// </summary>
    public ExploreLabels(MapCamera camera, Visual carried, MapGraphics graphics, IMotionPolicy motion)
    {
        // Never the thing being clicked. A click that landed on a name rather than the shape under
        // it would select whatever that name happened to overlap.
        IsHitTestVisible = false;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _device = graphics.Device;
        _motion = motion;
        _sheet = new NameSheet(graphics);
        _layer = new NameLayer(_compositor, camera.Properties, carried);
        _names = new NameSlots<NameSprite>(() => new NameSprite(_compositor, _sheet));

        ElementCompositionPreview.SetElementChildVisual(this, _layer.Root);
    }

    /// <summary>
    /// Put the names where <paramref name="placed"/> says one device-independent pixel of the drawing
    /// they name lies in the picture, for a drawing made at <paramref name="scale"/> device pixels to
    /// the device-independent pixel.
    /// </summary>
    public void Place(MapTransform placed, double scale) => _layer.Place(placed, scale);

    /// <summary>
    /// Follow <paramref name="carried"/> as a folder opening carries the picture, for the set of
    /// drawings the names are of now. The map swaps its two sets as a folder opens.
    /// </summary>
    public void Ride(Visual carried) => _layer.Follow(carried);

    /// <summary>
    /// Put <paramref name="caption"/>'s text on each shape <paramref name="drawing"/> chose to label,
    /// at <paramref name="scale"/> canvas pixels to the device-independent pixel.
    ///
    /// <para>The caption comes from the page rather than from here, because only it knows what the
    /// tree's nodes are: a drive's are a file name and a size, and a memory picture's are a process or
    /// a part of Windows. How it is written is the drawing's lettering (<see cref="MapCaption.Text"/>).</para>
    ///
    /// <para>The names are kept and written over rather than rebuilt, and a drawing with fewer puts
    /// the rest away. A name of the tree shown before is written by the name that wrote it, so its
    /// text, its measurement and its drawing are those it has (<see cref="NameSlots{TSlot}"/>). A scan
    /// repaints this several times a second, and each rebuild would throw away visuals and their
    /// brushes and draw a fresh set of names, for text that has usually not changed (G5).</para>
    ///
    /// <para>A name the map was not showing fades in, where the drawing is of the tree it was showing:
    /// detail a zoom or a folder opened uncovered. Another tree is a scan's next snapshot or another
    /// picture altogether, whose nodes are not the same nodes, and its names are put up at once, as a
    /// scan repainting several times a second needs.</para>
    /// </summary>
    public void Show(ExploreSurface drawing, double scale, Func<ExploreLabel, MapCaption> caption)
    {
        Visibility = Visibility.Visible;

        var sameTree = ReferenceEquals(drawing.Tree, _tree);
        var arrival = _motion.For(MotionToken.Detail);

        _names.Assign(drawing.Labels, NodeOf, sameTree);

        foreach (var spare in _names.Surplus)
        {
            spare.Collapse(_sheet);
            NameFades.Leave(spare.Visual);
        }

        // A device lost on the way is replaced, and the map's redraw that follows writes every name
        // again on the new one.
        _writing = (drawing, scale, caption);
        _device.UnlessLost(Write);
        _writing = default;

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var name = _names.Assigned[i];
            var measured = name.Text.Size;
            var (across, down) = label.Fade((float)(measured.Width * scale), (float)(measured.Height * scale));

            name.Place(label, drawing.Width, drawing.Height, scale);
            _layer.Fades.Join(name.Visual, across, down);
            name.Show();

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

        _layer.Fades.Settle();
        _tree = drawing.Tree;
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
        foreach (var name in _names.All)
        {
            NameFades.Leave(name.Visual);
        }

        _layer.Fades.Settle();
        _names.Clear();
        _sheet.Clear();
        _tree = null;
    }

    /// <summary>
    /// Measure, fit into the sheet and draw each name of the drawing being written, on
    /// <paramref name="device"/>. A drawing whose names no longer fit round the rectangles left over
    /// from earlier ones empties the sheet and draws them all again.
    /// </summary>
    private void Write(CanvasDevice device)
    {
        var (drawing, scale, caption) = _writing;

        if (_sheet.Prepare())
        {
            foreach (var name in _names.All)
            {
                name.Renew(_sheet);
            }
        }

        var textScale = SystemLook.Current.State.TextScale;
        var fitted = true;

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var name = _names.Assigned[i];

            var writing = new NameWriting(
                caption(label).Text(drawing.Lettering, label.Lines),
                drawing.Lettering,
                (float)(label.Width / scale),
                label.Centred,
                label.Colour.ToColor(),
                (float)(drawing.LineSpacing / scale),
                textScale);

            name.Text.Write(device, _formats, writing);

            fitted &= name.Fit(_sheet, scale);
        }

        if (!fitted)
        {
            _sheet.Clear();

            for (var i = 0; i < drawing.Labels.Count; i++)
            {
                _names.Assigned[i].Fit(_sheet, scale);
            }
        }

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            _names.Assigned[i].Draw(_sheet);
        }
    }
}
