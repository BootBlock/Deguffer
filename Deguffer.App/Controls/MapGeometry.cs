using System.Numerics;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The shapes the map lays over its picture, as composition paths: the outline round what is hovered
/// or picked, the dimming round a selection, and the hatch over what a removal acts on. Each is built in
/// the canvas's own pixels, so it sits on the picture wherever the camera puts it.
///
/// <para>Built through whichever device <see cref="MapGraphics"/> draws through now, which a loss
/// replaces. A geometry is the factory's, not the device's, so one already built outlives a
/// replacement.</para>
/// </summary>
internal sealed class MapGeometry
{
    private readonly Func<CanvasDevice> _device;

    /// <param name="device">The device the map draws through now.</param>
    public MapGeometry(Func<CanvasDevice> device)
    {
        _device = device;
        Nothing = new CompositionPath(CanvasGeometry.CreatePath(new CanvasPathBuilder(device())));
    }

    /// <summary>
    /// A path with nothing in it, for a geometry with nothing to draw. Never a null path: setting
    /// <see cref="CompositionPathGeometry.Path"/> to null ended the process with an access violation
    /// inside the setter on the Windows App SDK 1.8. Not at every such call, but once at the first of
    /// them and once many calls in, so no number of calls that survive makes it safe.
    /// </summary>
    public CompositionPath Nothing { get; }

    /// <summary>
    /// A geometry round every outline, in the canvas's own pixels, for an outline drawn in the
    /// composition tree. <see cref="Nothing"/> where there is nothing to outline.
    ///
    /// <para>One geometry for all of them rather than one per outline, because the list view selects
    /// any number of rows at once and a shape apiece would be hundreds of them (G4).</para>
    /// </summary>
    public CompositionPath Trace(IReadOnlyList<ExploreOutline> outlines)
    {
        if (outlines.Count == 0)
        {
            return Nothing;
        }

        // Not disposed: making the geometry takes the builder over.
        var path = new CanvasPathBuilder(_device());

        foreach (var outline in outlines)
        {
            AddFigure(path, outline);
        }

        return new CompositionPath(CanvasGeometry.CreatePath(path));
    }

    /// <summary>
    /// A geometry over everything but <paramref name="outlines"/> to <paramref name="far"/> pixels
    /// from the canvas's corner each way, in the canvas's own pixels: what the map dims round a
    /// selection. <see cref="Nothing"/> where there is nothing to leave out, because then nothing is
    /// dimmed.
    ///
    /// <para>The caller says how far, because a canvas is a part of its picture as small as its zoom
    /// is deep, and the dimming has to cover the whole picture while the canvas is shown zoomed out.
    /// The shape visual the dimming is drawn in cuts it to the picture.</para>
    /// </summary>
    public CompositionPath Around(IReadOnlyList<ExploreOutline> outlines, float far)
    {
        if (Shapes(outlines) is not { } shapes)
        {
            return Nothing;
        }

        var everything = CanvasGeometry.CreateRectangle(_device(), -far, -far, 2 * far, 2 * far);

        return new CompositionPath(everything.CombineWith(shapes, Matrix3x2.Identity, CanvasGeometryCombine.Exclude));
    }

    /// <summary>
    /// A geometry over everything to <paramref name="far"/> pixels from the canvas's corner each way:
    /// what <see cref="Around"/> covers with nothing left out of it.
    /// </summary>
    public CompositionPath Everything(float far) =>
        new(CanvasGeometry.CreateRectangle(_device(), -far, -far, 2 * far, 2 * far));

    /// <summary>
    /// Stripes across <paramref name="outlines"/> and nowhere else, in the canvas's own pixels: one
    /// stripe in every <paramref name="spacing"/>, <paramref name="width"/> wide, starting
    /// <paramref name="phase"/> along. <see cref="Nothing"/> where there is nothing to stripe.
    /// </summary>
    public CompositionPath Hatch(IReadOnlyList<ExploreOutline> outlines, float spacing, float width, float phase)
    {
        if (Shapes(outlines) is not { } shapes)
        {
            return Nothing;
        }

        // Upright stripes over a square round the shapes wide enough to cover them at any turn, turned
        // a quarter of the way round about its middle: a hatch reads as a hatch at any size of shape.
        var bounds = shapes.ComputeBounds();
        var side = (float)Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));
        var centre = new Vector2((float)(bounds.X + (bounds.Width / 2)), (float)(bounds.Y + (bounds.Height / 2)));
        var left = centre.X - (side / 2);
        var top = centre.Y - (side / 2);
        var stripes = new List<CanvasGeometry>();

        for (var x = left + phase; x < left + side; x += spacing)
        {
            stripes.Add(CanvasGeometry.CreateRectangle(_device(), x, top, width, side));
        }

        var turned = CanvasGeometry.CreateGroup(_device(), [.. stripes], CanvasFilledRegionDetermination.Winding)
            .Transform(Matrix3x2.CreateRotation(MathF.PI / 4, centre));

        return new CompositionPath(turned.CombineWith(shapes, Matrix3x2.Identity, CanvasGeometryCombine.Intersect));
    }

    /// <summary>
    /// The shapes <paramref name="outlines"/> bound, filled, as one geometry, or null where there are
    /// none.
    ///
    /// <para>A node's boundaries are filled together, alternately, which is how a ring keeps its hole.
    /// Each is then outlined, which leaves a geometry filled the same whichever rule fills it, and the
    /// nodes are grouped under the rule that fills wherever any of them does: their union, so a folder
    /// picked with a file inside it is one shape rather than a folder with the file cut out.</para>
    /// </summary>
    private CanvasGeometry? Shapes(IReadOnlyList<ExploreOutline> outlines)
    {
        var nodes = new List<CanvasGeometry>();
        var i = 0;

        while (i < outlines.Count)
        {
            // Not disposed: making the geometry takes the builder over.
            var path = new CanvasPathBuilder(_device());
            path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);

            var node = outlines[i].Node;
            var figures = 0;

            // A node's boundaries come back one after another: see ExploreOutline.
            for (; i < outlines.Count && outlines[i].Node == node; i++)
            {
                figures += AddFigure(path, outlines[i]) ? 1 : 0;
            }

            if (figures > 0)
            {
                nodes.Add(CanvasGeometry.CreatePath(path).Outline());
            }
        }

        return nodes.Count == 0
            ? null
            : CanvasGeometry.CreateGroup(_device(), [.. nodes], CanvasFilledRegionDetermination.Winding);
    }

    /// <summary>
    /// Add <paramref name="outline"/> to <paramref name="path"/> as one closed figure, and say whether
    /// there was anything to add. The one way a boundary becomes a figure, so the line round a shape
    /// and the area it fills are the same shape.
    /// </summary>
    private static bool AddFigure(CanvasPathBuilder path, ExploreOutline outline)
    {
        var points = outline.Points;

        if (points.Count == 0)
        {
            return false;
        }

        path.BeginFigure(points[0].X, points[0].Y);

        for (var i = 1; i < points.Count; i++)
        {
            path.AddLine(points[i].X, points[i].Y);
        }

        path.EndFigure(CanvasFigureLoop.Closed);

        return true;
    }
}
