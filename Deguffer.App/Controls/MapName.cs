using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// One reusable name on the map (see <see cref="ExploreLabels"/>): its text, with everything a
/// repaint never changes already set — including the brush and the transform, which are written
/// through rather than replaced — and what moves and fades it on the compositor.
///
/// <para>Separate from <see cref="ExploreLabels"/>, which decides which name goes where and holds what
/// every name shares, because this is one name's own properties and the expressions that read them
/// (G1).</para>
/// </summary>
internal sealed class MapName
{
    /// <summary>
    /// The canvas's size on the screen, in device-independent pixels: where a kept span is cut. A
    /// property of the set every name shares, which <see cref="ExploreLabels"/> writes.
    /// </summary>
    public const string Extent = "Extent";

    /// <summary>
    /// How many device pixels a device-independent one is: what an anchor is rounded to. A property
    /// of the set every name shares, as <see cref="Extent"/> is.
    /// </summary>
    public const string Pixel = "Pixel";

    /// <summary>
    /// A name's properties, which the expressions below read: the rule it is placed across its
    /// drawing by, as the span's start and end, the inset, and 1 where it is centred on the span
    /// (<see cref="LabelAxis"/>).
    /// </summary>
    private const string Across = "Across";

    /// <summary>The rule it is placed down its drawing by, as <see cref="Across"/>.</summary>
    private const string Down = "Down";

    /// <summary>1 on each axis where the span is cut to the canvas first, and 0 where it is not.</summary>
    private const string Kept = "Kept";

    /// <summary>How far left of its anchor the text starts, so a centred name is centred on it.</summary>
    private const string Shift = "Shift";

    /// <summary>How far above its anchor the text starts, so a name of several lines is centred down it.</summary>
    private const string Lift = "Lift";

    /// <summary>The magnifications it fades between, across then down: see <see cref="ExploreLabel.Fade"/>.</summary>
    private const string Fade = "Fade";

    /// <summary>How far it has arrived, from 0 to 1.</summary>
    private const string Arrival = "Arrival";

    /// <summary>
    /// Where a name's anchor is on the screen: its rule (<see cref="LabelAxis.At"/>) worked out for
    /// where its drawing lies in the picture and where the camera puts the picture, then carried
    /// wherever a folder opening carries it. Each a scale then a move, written out because the
    /// expression language composes nothing for itself.
    /// </summary>
    private static readonly string PlacementExpression =
        "Vector3("
        + $"({Pinned(Across, "X")}) * carried.Scale.X + carried.Offset.X - label.{Shift}, "
        + $"({Pinned(Down, "Y")}) * carried.Scale.Y + carried.Offset.Y - label.{Lift}, "
        + "0)";

    /// <summary>
    /// How opaque a name is: by how far its picture is magnified on each axis against its fade, by how
    /// far it has arrived, and by how far the picture it names has.
    /// </summary>
    private const string OpacityExpression =
        "Clamp(Min("
        + $"((placed.Scale.X * camera.Scale.X * carried.Scale.X) - label.{Fade}.X) / (label.{Fade}.Y - label.{Fade}.X), "
        + $"((placed.Scale.Y * camera.Scale.Y * carried.Scale.Y) - label.{Fade}.Z) / (label.{Fade}.W - label.{Fade}.Z)), 0, 1)"
        + $" * label.{Arrival} * carried.Opacity";

    private readonly ExpressionAnimation _placement;

    private readonly ExpressionAnimation _opacity;

    private readonly Visual _visual;

    /// <summary>
    /// Where the name is and how it fades, in its own coordinates: <c>Across</c>, <c>Down</c>,
    /// <c>Kept</c>, <c>Shift</c>, <c>Lift</c>, <c>Fade</c> and <c>Arrival</c>.
    /// </summary>
    private readonly CompositionPropertySet _properties;

    /// <summary>Whether the name is on the map, rather than put away until a drawing needs it.</summary>
    private bool _shown = true;

    /// <summary>
    /// A name drawn through the map's <paramref name="device"/> in its <paramref name="formats"/>,
    /// placed by the <paramref name="camera"/>, <paramref name="placed"/> and <paramref name="screen"/>
    /// every name shares, and carried with <paramref name="carried"/>.
    /// </summary>
    public MapName(
        Compositor compositor,
        CompositionDevice device,
        ClassicFormats formats,
        CompositionPropertySet camera,
        CompositionPropertySet placed,
        CompositionPropertySet screen,
        Visual carried)
    {
        Text = new LabelText(device, formats);

        _properties = compositor.CreatePropertySet();
        _properties.InsertVector4(Across, Vector4.Zero);
        _properties.InsertVector4(Down, Vector4.Zero);
        _properties.InsertVector2(Kept, Vector2.Zero);
        _properties.InsertScalar(Shift, 0);
        _properties.InsertScalar(Lift, 0);
        _properties.InsertVector4(Fade, new Vector4(0, 1, 0, 1));
        _properties.InsertScalar(Arrival, 1);

        _placement = compositor.CreateExpressionAnimation(PlacementExpression);
        _opacity = compositor.CreateExpressionAnimation(OpacityExpression);

        foreach (var expression in new[] { _placement, _opacity })
        {
            expression.SetReferenceParameter("label", _properties);
            expression.SetReferenceParameter("camera", camera);
            expression.SetReferenceParameter("placed", placed);
            expression.SetReferenceParameter("screen", screen);
        }

        _visual = ElementCompositionPreview.GetElementVisual(Text.Element);
        ElementCompositionPreview.SetIsTranslationEnabled(Text.Element, true);

        Follow(carried);
    }

    /// <summary>The text of the name, in the drawing's lettering.</summary>
    public LabelText Text { get; }

    /// <summary>
    /// Place the name as <paramref name="label"/> says, for text <paramref name="measured"/>
    /// device-independent pixels in size, on a canvas at <paramref name="scale"/>.
    /// </summary>
    public void Place(ExploreLabel label, Size measured, double scale)
    {
        var (across, down) = label.Fade((float)(measured.Width * scale), (float)(measured.Height * scale));

        _properties.InsertVector4(Across, Rule(label.Across, scale));
        _properties.InsertVector4(Down, Rule(label.Down, scale));
        _properties.InsertVector2(Kept, new Vector2(label.Across.KeptOnCanvas ? 1 : 0, label.Down.KeptOnCanvas ? 1 : 0));
        _properties.InsertScalar(Shift, label.Centred ? Half(measured.Width, scale) : 0);
        _properties.InsertScalar(Lift, label.Middle ? Half(measured.Height, scale) : 0);
        _properties.InsertVector4(Fade, new Vector4(across.Gone, across.Whole, down.Gone, down.Whole));
    }

    /// <summary>Move and fade with <paramref name="carried"/>, from now on.</summary>
    public void Follow(Visual carried)
    {
        foreach (var expression in new[] { _placement, _opacity })
        {
            expression.SetReferenceParameter("carried", carried);
        }

        if (_shown)
        {
            Animate();
        }
    }

    /// <summary>Put the name back on the map, moving and fading as it did before it was put away.</summary>
    public void Show()
    {
        if (!_shown)
        {
            _shown = true;
            Text.Element.Visibility = Visibility.Visible;
            Animate();
        }
    }

    /// <summary>
    /// Put the name away until a drawing needs it. Its expressions stop with it, so the compositor
    /// does not go on working out, on every frame, where hundreds of names nobody sees would be.
    /// </summary>
    public void Collapse()
    {
        if (_shown)
        {
            _shown = false;
            Text.Element.Visibility = Visibility.Collapsed;
            _visual.StopAnimation(VisualTranslation.Property);
            _visual.StopAnimation(nameof(Visual.Opacity));
        }
    }

    /// <summary>Be there at once.</summary>
    public void Arrive()
    {
        _properties.StopAnimation(Arrival);
        _properties.InsertScalar(Arrival, 1);
    }

    /// <summary>Fade in, as <paramref name="played"/> says, along <paramref name="ease"/>.</summary>
    public void Enter(Motion played, CompositionEasingFunction ease)
    {
        if (played.IsInstant)
        {
            Arrive();
            return;
        }

        var fade = _properties.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = played.Duration;

        _properties.StartAnimation(Arrival, fade);
    }

    private void Animate()
    {
        _visual.StartAnimation(VisualTranslation.Property, _placement);
        _visual.StartAnimation(nameof(Visual.Opacity), _opacity);
    }

    /// <summary>
    /// Half of <paramref name="length"/> device-independent pixels, down to a whole device pixel, so a
    /// centred name starts on one as its anchor does (see <see cref="Pinned"/>).
    /// </summary>
    private static float Half(double length, double scale) => (float)(Math.Floor(length * scale / 2) / scale);

    /// <summary>A rule as the expressions read it, in device-independent pixels of a canvas at <paramref name="scale"/>.</summary>
    private static Vector4 Rule(LabelAxis axis, double scale) => new(
        (float)(axis.From / scale),
        (float)(axis.To / scale),
        (float)(axis.Inset / scale),
        axis.Centred ? 1 : 0);

    /// <summary>
    /// <see cref="LabelAxis.At"/> as an expression, along <paramref name="axis"/> for the rule in the
    /// name's <paramref name="rule"/> property: its span carried into the picture and onto the screen,
    /// cut to the canvas where it is kept, and the anchor measured from its start or its middle.
    /// </summary>
    private static string Pinned(string rule, string axis)
    {
        var from = OnScreen($"label.{rule}.X", axis);
        var to = OnScreen($"label.{rule}.Y", axis);
        var kept = $"label.{Kept}.{axis}";
        var start = $"Lerp({from}, Max({from}, 0), {kept})";
        var end = $"Lerp({to}, Min({to}, placed.{Extent}.{axis}), {kept})";
        var centred = $"label.{rule}.W";

        // The start, or the middle of the span, as one sum: start + (end - start) / 2 where centred.
        // Rounded to a whole device pixel, because a name drawn off the pixel grid is resampled, and
        // one in SpaceMonger's unsmoothed lettering comes out smoothed after all; the middle of a span
        // falls on a half pixel as often as not.
        var anchor = $"({start} * (1 - ({centred} / 2))) + ({end} * ({centred} / 2)) + label.{rule}.Z";

        return $"Floor(({anchor}) * placed.{Pixel} + 0.5) / placed.{Pixel}";
    }

    /// <summary>A point of a drawing in its device-independent pixels, on the screen.</summary>
    private static string OnScreen(string value, string axis) =>
        $"(({value} * screen.Scale.{axis}) + screen.Offset.{axis})";
}
