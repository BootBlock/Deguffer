using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The visual every name on the map hangs under, which carries them with the picture: where the
/// drawing they name lies in the picture, where the map's camera puts the picture, and where a folder
/// opening carries it, combined by the compositor on every frame in three expressions, whatever the
/// number of names.
///
/// <para>The text keeps the reader's size while the picture is magnified, so the names cannot hang
/// under a visual that is scaled. This visual's <em>size</em> is the picture's magnification against
/// the drawing's instead, and each name is offset by a fraction of that size
/// (<see cref="Visual.RelativeOffsetAdjustment"/>): the point of the picture it moves with, in the
/// drawing's device-independent pixels. So a name moves where its point moves as the picture is
/// magnified and dragged, and nothing about a name is worked out per frame. Its own offset is fixed
/// where its drawing put it: see <see cref="NameSprite.Place"/>.</para>
///
/// <para>The move is rounded to a whole device pixel, so a name placed on one by its drawing stays on
/// one while the picture is dragged.</para>
///
/// <para>Separate from <see cref="NameFades"/>, which decides how opaque each name is, because this is
/// where they all are (G1).</para>
/// </summary>
internal sealed class NameLayer
{
    /// <summary>The names the expressions read the camera, the drawing's place and the carrying visual by.</summary>
    private const string Camera = "camera";

    private const string Placed = "placed";

    private const string Carried = "carried";

    /// <summary>A property of <see cref="_placed"/>: how many device pixels a device-independent one is, which the move is rounded to.</summary>
    private const string Pixel = "Pixel";

    private static readonly string OffsetExpression =
        $"Vector3({Moved("X")} * {Carried}.Scale.X + {Carried}.Offset.X, {Moved("Y")} * {Carried}.Scale.Y + {Carried}.Offset.Y, 0)";

    private static readonly string SizeExpression =
        $"Vector2({Magnified("X")}, {Magnified("Y")})";

    private static readonly string OpacityExpression = $"{Carried}.Opacity";

    /// <summary>
    /// Where the drawing the names were laid out for lies in the picture, per device-independent pixel
    /// of that drawing: a <c>Scale</c> and an <c>Offset</c>, and the <see cref="Pixel"/> it was drawn at.
    /// </summary>
    private readonly CompositionPropertySet _placed;

    private readonly ExpressionAnimation[] _expressions;

    /// <summary>A layer on <paramref name="compositor"/> that moves with <paramref name="camera"/>, and with <paramref name="carried"/>.</summary>
    public NameLayer(Compositor compositor, CompositionPropertySet camera, Visual carried)
    {
        _placed = compositor.CreatePropertySet();
        _placed.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _placed.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        _placed.InsertScalar(Pixel, 1);

        Root = compositor.CreateContainerVisual();

        _expressions =
        [
            compositor.CreateExpressionAnimation(OffsetExpression),
            compositor.CreateExpressionAnimation(SizeExpression),
            compositor.CreateExpressionAnimation(OpacityExpression),
        ];

        foreach (var expression in _expressions)
        {
            expression.SetReferenceParameter(Camera, camera);
            expression.SetReferenceParameter(Placed, _placed);
        }

        Fades = new NameFades(compositor, Root);

        Follow(carried);
    }

    /// <summary>The visual every name hangs under, through its fade step's.</summary>
    public ContainerVisual Root { get; }

    /// <summary>How the names fade, by step, under <see cref="Root"/>.</summary>
    public NameFades Fades { get; }

    /// <summary>How many expressions the compositor works out on every frame for the names: three here, and one per fade step in use.</summary>
    public int Running => _expressions.Length + Fades.Running;

    /// <summary>Lay the names out for a drawing placed in the picture by <paramref name="placed"/>, drawn at <paramref name="scale"/>.</summary>
    public void Place(MapTransform placed, double scale)
    {
        _placed.Place(placed);
        _placed.InsertScalar(Pixel, (float)scale);
    }

    /// <summary>Move and fade with <paramref name="carried"/>, from now on, as a folder opening carries the picture.</summary>
    public void Follow(Visual carried)
    {
        foreach (var expression in _expressions)
        {
            expression.SetReferenceParameter(Carried, carried);
        }

        Root.StartAnimation(nameof(Visual.Offset), _expressions[0]);
        Root.StartAnimation(nameof(Visual.Size), _expressions[1]);
        Root.StartAnimation(nameof(Visual.Opacity), _expressions[2]);
    }

    /// <summary>
    /// Where the drawing's corner is on the screen along <paramref name="axis"/>, on a whole device
    /// pixel: its place in the picture moved by the camera.
    /// </summary>
    private static string Moved(string axis) =>
        $"(Floor((({Placed}.Offset.{axis} * {Camera}.Scale.{axis}) + {Camera}.Offset.{axis}) * {Placed}.{Pixel} + 0.5) / {Placed}.{Pixel})";

    /// <summary>How magnified the picture is against the drawing along <paramref name="axis"/>, with whatever carries it.</summary>
    private static string Magnified(string axis) =>
        $"({Placed}.Scale.{axis} * {Camera}.Scale.{axis} * {Carried}.Scale.{axis})";
}
