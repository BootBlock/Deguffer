using System.Numerics;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// One reusable name on the map (see <see cref="ExploreLabels"/>): its text, the rectangle of the
/// names' sheet it is drawn into, and the visual that shows that rectangle where the drawing put it.
///
/// <para>Nothing of a name moves on the compositor by itself. The drawing's names hang under one visual
/// that follows the camera (see <see cref="NameLayer"/>): each name is placed once, by how far its
/// anchor is from the point of the picture it moves with (<see cref="LabelAxis.Carried"/>), and that
/// visual's size carries the point as the picture is magnified, while the text keeps the reader's
/// size. Its opacity is its fade step's (see <see cref="NameFades"/>), times its own only while it
/// arrives.</para>
///
/// <para>Separate from <see cref="ExploreLabels"/>, which decides which name goes where and holds what
/// every name shares, because this is one name's own properties (G1).</para>
/// </summary>
internal sealed class NameSprite
{
    private readonly CompositionSurfaceBrush _brush;

    /// <summary>The rectangle of the sheet the name is drawn into, or null where it has none.</summary>
    private SheetRegion? _region;

    /// <summary>The sheet's <see cref="NameSheet.Epoch"/> <see cref="_region"/> was taken in.</summary>
    private int _epoch;

    /// <summary>The scale the name was last drawn at, which a change of draws it again.</summary>
    private double _scale;

    /// <summary>A name on <paramref name="compositor"/>, drawn into <paramref name="sheet"/>.</summary>
    public NameSprite(Compositor compositor, NameSheet sheet)
    {
        _brush = compositor.CreateUnstretchedBrush(sheet.Surface);

        // A name moved off the pixel grid as the picture is magnified is drawn on it still, so
        // SpaceMonger's unsmoothed type is not smoothed by resampling.
        _brush.SnapToPixels = true;

        Visual = compositor.CreateSpriteVisual();
        Visual.Brush = _brush;
    }

    /// <summary>The text of the name, in the drawing's lettering.</summary>
    public NameText Text { get; } = new();

    /// <summary>What shows the name, under its fade step's visual.</summary>
    public SpriteVisual Visual { get; }

    /// <summary>Show <paramref name="sheet"/>'s surface, made anew, whose rectangles are all void.</summary>
    public void Renew(NameSheet sheet)
    {
        _brush.Surface = sheet.Surface;
        _region = null;
        Text.Forget();
    }

    /// <summary>
    /// Take a rectangle of <paramref name="sheet"/> the size of the text as last written, at
    /// <paramref name="scale"/> pixels to the device-independent pixel, unless the one it has is that
    /// size already.
    /// </summary>
    /// <returns>Whether the name has a rectangle: false where the sheet is full.</returns>
    public bool Fit(NameSheet sheet, double scale)
    {
        var width = Math.Min(Pixels(Text.Size.Width, scale), sheet.Width);
        var height = Pixels(Text.Size.Height, scale);

        if (_region is { } region && _epoch == sheet.Epoch)
        {
            if (region.Width == width && region.Height == height && _scale == scale)
            {
                return true;
            }

            sheet.Give(region);
        }

        _region = sheet.Take(width, height);
        _epoch = sheet.Epoch;
        _scale = scale;
        Text.Forget();

        if (_region is not { } taken)
        {
            Visual.IsVisible = false;
            return false;
        }

        // The rectangle's pixels at one to a device pixel: the window scales every visual by the
        // scale, so the brush shows a pixel of the sheet as a device-independent pixel's share of one.
        Visual.Size = new Vector2((float)(taken.Width / scale), (float)(taken.Height / scale));
        _brush.TransformMatrix = Matrix3x2.CreateTranslation(-taken.X, -taken.Y) * Matrix3x2.CreateScale((float)(1 / scale));

        return true;
    }

    /// <summary>Draw the text into its rectangle of <paramref name="sheet"/>, unless it is drawn there already.</summary>
    public void Draw(NameSheet sheet)
    {
        if (_region is { } region && _epoch == sheet.Epoch && !Text.IsDrawn)
        {
            using var session = sheet.Open(region, _scale);

            Text.Draw(session);
        }
    }

    /// <summary>
    /// Place the name as <paramref name="label"/> says, on the canvas of <paramref name="width"/> by
    /// <paramref name="height"/> pixels it was laid out for, at <paramref name="scale"/> pixels to the
    /// device-independent pixel.
    /// </summary>
    public void Place(ExploreLabel label, int width, int height, double scale)
    {
        var size = Text.Size;
        var shift = label.Centred ? Half(size.Width, scale) : 0;
        var lift = label.Middle ? Half(size.Height, scale) : 0;
        var carriedX = label.Across.Carried / scale;
        var carriedY = label.Down.Carried / scale;

        Visual.RelativeOffsetAdjustment = new Vector3((float)carriedX, (float)carriedY, 0);
        Visual.Offset = new Vector3(
            (float)(Pinned(label.Across.Drawn(width), scale) - carriedX - shift),
            (float)(Pinned(label.Down.Drawn(height), scale) - carriedY - lift),
            0);

        // Zero for anything in rectangles, and per label for a sunburst, which turns each one about
        // its middle to lie along its own ring.
        Visual.CenterPoint = new Vector3((float)(size.Width / 2), (float)(size.Height / 2), 0);
        Visual.RotationAngleInDegrees = label.Rotation;
    }

    /// <summary>Put the name on the map, where it has a rectangle to show.</summary>
    public void Show() => Visual.IsVisible = _region is not null;

    /// <summary>
    /// Put the name away until a drawing needs it, and give its rectangle back to
    /// <paramref name="sheet"/> for a name that is shown.
    /// </summary>
    public void Collapse(NameSheet sheet)
    {
        Visual.IsVisible = false;
        Arrive();

        if (_region is { } region && _epoch == sheet.Epoch)
        {
            sheet.Give(region);
        }

        _region = null;
        Text.Forget();
    }

    /// <summary>Be there at once.</summary>
    public void Arrive()
    {
        Visual.StopAnimation(nameof(Visual.Opacity));
        Visual.Opacity = 1;
    }

    /// <summary>Fade in, as <paramref name="played"/> says, along <paramref name="ease"/>.</summary>
    public void Enter(Motion played, CompositionEasingFunction ease)
    {
        if (played.IsInstant)
        {
            Arrive();
            return;
        }

        var fade = Visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = played.Duration;

        Visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    /// <summary>How many whole pixels <paramref name="length"/> device-independent pixels cover at <paramref name="scale"/>.</summary>
    private static int Pixels(double length, double scale) => Math.Max(1, (int)Math.Ceiling((length * scale) - (1.0 / 1024)));

    /// <summary>
    /// An anchor <paramref name="at"/> canvas pixels along, in device-independent pixels, on a whole
    /// device pixel: a name drawn off the pixel grid is resampled, and one in SpaceMonger's unsmoothed
    /// lettering comes out smoothed after all. The middle of a span falls on a half pixel as often as
    /// not.
    /// </summary>
    private static double Pinned(float at, double scale) => Math.Floor(at + 0.5) / scale;

    /// <summary>
    /// Half of <paramref name="length"/> device-independent pixels, down to a whole device pixel, so a
    /// centred name starts on one as its anchor does (see <see cref="Pinned"/>).
    /// </summary>
    private static double Half(double length, double scale) => Math.Floor(length * scale / 2) / scale;
}
