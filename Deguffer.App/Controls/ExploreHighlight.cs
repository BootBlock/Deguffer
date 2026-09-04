using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

// Aliased because System.IO.Path arrives through the project's implicit usings and would otherwise
// make every mention of the shape ambiguous.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Deguffer.App.Controls;

/// <summary>
/// The outlines drawn over the map: what is picked, and what the pointer is over.
///
/// <para>Over the bitmap rather than in it, which is what the reference implementation does and for
/// the reason it does it — WinDirStat renders the shapes into a cached surface once and draws only
/// the selection live over the top. Baked into the bitmap instead, every click would rasterise the
/// whole volume again to move one outline.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> because the two answer different questions. That one
/// is about which tree is drawn and what the pointer found; this one is about marking a shape out
/// once somebody else has decided which shape it is (G1).</para>
/// </summary>
internal sealed class ExploreHighlight : Canvas
{
    /// <summary>
    /// Two strokes, dark under light, rather than one in a colour chosen to contrast.
    ///
    /// <para>§6.5 asks for the UI to read on a flat background in either theme, and a treemap is a
    /// harder case than that: the shape underneath is any of eight hues at any of four lightnesses,
    /// shaded from bright at its middle to dark at its edges, and it is the <em>edge</em> an outline
    /// runs along. No single colour survives that. A dark line with a light one inside it is legible
    /// against every one of them, is the same in both themes, and is what a selection marquee has
    /// looked like for long enough that nobody has to be told what it means.</para>
    /// </summary>
    private const double PickedHaloWidth = 3.5;

    private const double PickedEdgeWidth = 1.75;

    /// <summary>
    /// Thinner and fainter than the picked outline, because it says something weaker. What the
    /// pointer is over is about to be picked; what is picked is what Delete acts on, and the two
    /// must not read as the same claim.
    /// </summary>
    private const double HoveredHaloWidth = 2.5;

    private const double HoveredEdgeWidth = 1;

    private readonly ScaleTransform _stretch = new();

    private readonly Path _hoveredHalo = Stroke(Colors.Black, 0.35);
    private readonly Path _hoveredEdge = Stroke(Colors.White, 0.85);
    private readonly Path _pickedHalo = Stroke(Colors.Black, 0.6);
    private readonly Path _pickedEdge = Stroke(Colors.White, 1);

    public ExploreHighlight()
    {
        // Never the thing being clicked. An outline sits on the boundary between two shapes, so a
        // click it swallowed would be a click on whichever of them the outline happened to cover.
        IsHitTestVisible = false;

        // The geometry is in the bitmap's own pixels, and this is what puts it over the bitmap
        // wherever that has been stretched to. While a resize settles the map is the old picture
        // scaled to fit, and the outlines have to be scaled with it or they mark out the wrong
        // shapes for as long as that lasts.
        RenderTransform = _stretch;

        Children.Add(_hoveredHalo);
        Children.Add(_hoveredEdge);
        Children.Add(_pickedHalo);
        Children.Add(_pickedEdge);
    }

    /// <summary>Mark out what the user picked.</summary>
    public void ShowPicked(IReadOnlyList<ExploreOutline> outlines)
    {
        _pickedHalo.Data = Trace(outlines);
        _pickedEdge.Data = Trace(outlines);
    }

    /// <summary>Mark out what the pointer is over.</summary>
    public void ShowHovered(IReadOnlyList<ExploreOutline> outlines)
    {
        _hoveredHalo.Data = Trace(outlines);
        _hoveredEdge.Data = Trace(outlines);
    }

    /// <summary>
    /// Lay the outlines over a bitmap of <paramref name="canvasWidth"/> by
    /// <paramref name="canvasHeight"/> pixels drawn across <paramref name="width"/> by
    /// <paramref name="height"/> of the control.
    ///
    /// <para>The stroke widths are divided by the same ratio, so a line stays the width it was asked
    /// for rather than thickening with the display's scale.</para>
    /// </summary>
    public void StretchOver(double canvasWidth, double canvasHeight, double width, double height)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        _stretch.ScaleX = width / canvasWidth;
        _stretch.ScaleY = height / canvasHeight;

        // From the narrower of the two, so a line is the width it was asked for at its thinnest
        // rather than at its thickest. The two differ only while a resize is settling, and an
        // outline briefly a shade thin reads better than one briefly heavy.
        var scale = Math.Max(0.0001, Math.Max(_stretch.ScaleX, _stretch.ScaleY));

        _hoveredHalo.StrokeThickness = HoveredHaloWidth / scale;
        _hoveredEdge.StrokeThickness = HoveredEdgeWidth / scale;
        _pickedHalo.StrokeThickness = PickedHaloWidth / scale;
        _pickedEdge.StrokeThickness = PickedEdgeWidth / scale;
    }

    /// <summary>Take every outline off, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        _hoveredHalo.Data = null;
        _hoveredEdge.Data = null;
        _pickedHalo.Data = null;
        _pickedEdge.Data = null;
    }

    private static Path Stroke(Color colour, double opacity) => new()
    {
        Stroke = new SolidColorBrush(colour),
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = opacity,
    };

    /// <summary>
    /// One geometry round every outline, in the bitmap's own pixels.
    ///
    /// <para>One shape for all of them rather than one per outline, because the list view selects
    /// any number of rows at once and a control apiece would put hundreds of elements on the page to
    /// draw a few hundred lines (G4).</para>
    ///
    /// <para>Built again for each of the two strokes rather than shared between them. A geometry is
    /// cheap to build and this is only ever a few hundred points; what it is not is a value, and two
    /// elements holding the same one is the kind of sharing that works until the day it does not.</para>
    /// </summary>
    private static PathGeometry? Trace(IReadOnlyList<ExploreOutline> outlines)
    {
        if (outlines.Count == 0)
        {
            return null;
        }

        var geometry = new PathGeometry();

        foreach (var outline in outlines)
        {
            var points = outline.Points;

            if (points.Count == 0)
            {
                continue;
            }

            var line = new PolyLineSegment();

            for (var i = 1; i < points.Count; i++)
            {
                line.Points.Add(new Point(points[i].X, points[i].Y));
            }

            geometry.Figures.Add(new PathFigure
            {
                StartPoint = new Point(points[0].X, points[0].Y),
                Segments = { line },

                // Closed and unfilled: the outline is a line round the shape, and a fill would hide
                // the colour the picture spent its whole palette establishing.
                IsClosed = true,
                IsFilled = false,
            });
        }

        return geometry;
    }
}
