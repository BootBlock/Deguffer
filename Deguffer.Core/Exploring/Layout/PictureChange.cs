using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What a map showing one picture is to do when it is asked for another, or for the same one again:
/// whether the zoom stays, whether the shapes move to where they are now, whether the view changes
/// by carrying its shapes, where the new picture opens, and which way the camera flies to it.
///
/// <para>The answer is the map's to act on. It decides nothing about what the screen holds while it
/// does, and a click, a key or a hover still resolves against the drawing on screen (§7.1).</para>
/// </summary>
public sealed record PictureChange
{
    /// <summary>Made only by <see cref="Between"/>, which decides every answer.</summary>
    private PictureChange()
    {
    }

    /// <summary>The picture on screen when the other was asked for.</summary>
    public MapPicture Shown { get; private init; }

    /// <summary>The picture asked for.</summary>
    public MapPicture Asked { get; private init; }

    /// <summary>
    /// Whether <see cref="Asked"/> is another picture than <see cref="Shown"/>. Where it is not, the
    /// zoom stays, as it does for a new colouring, scheme, spacing or finish.
    /// </summary>
    public bool Another { get; private init; }

    /// <summary>
    /// Whether <see cref="Asked"/> is the same place as <see cref="Shown"/> laid out again, in a new
    /// tree of it drawn the same way, so the zoom stays and the shapes move to where they are now.
    /// Never for a page going back or forward, which asks for a zoom of its own.
    /// </summary>
    public bool SamePlace { get; private init; }

    /// <summary>
    /// Whether <see cref="Asked"/> is the same place in another of the views that draw a picture, so
    /// its largest shapes travel from where the one view draws them to where the other does, from the
    /// whole of one picture to the whole of the other (<see cref="ViewMorph"/>).
    /// </summary>
    public bool Morph { get; private init; }

    /// <summary>
    /// The zoom <see cref="Asked"/> is drawn at from the start, rather than drawn whole and then glided
    /// in: the zoom a page going back or forward asked for, on a picture that zooms, and the whole of
    /// it otherwise.
    /// </summary>
    public MapViewport Opened { get; private init; }

    /// <summary>
    /// The zoom the same picture moves to, as a zoom to a shape does, for a page going back or forward
    /// to where the reader had zoomed on it. Null for another picture, which opens at
    /// <see cref="Opened"/>, and for a picture that does not zoom.
    /// </summary>
    public MapViewport? Glide { get; private init; }

    /// <summary>Whether <see cref="Asked"/> is drawn as a picture the map lets the reader zoom.</summary>
    private bool Zooms { get; init; }

    /// <summary>
    /// What the map is to do, showing <paramref name="shown"/>, when it is asked for
    /// <paramref name="asked"/>.
    /// </summary>
    /// <param name="viewport">
    /// The zoom asked for, by a page going back or forward to where the reader had zoomed, or null to
    /// keep the zoom on the same picture and start another at the whole of it.
    /// </param>
    /// <param name="zoomable">Whether the map lets the reader zoom at all.</param>
    public static PictureChange Between(MapPicture shown, MapPicture asked, MapViewport? viewport, bool zoomable)
    {
        // A zoom is into one picture. Another tree, another folder or another view is another
        // picture, and the part of this one the reader had magnified means nothing in it. So is the
        // same root with the volume beside it or without it, which is the root opened or closed.
        var another = asked != shown;
        var zooms = zoomable && asked.Tree is not null && ExploreSurface.Zooms(asked.Tree, asked.View);

        return new PictureChange
        {
            Shown = shown,
            Asked = asked,
            Another = another,

            // The same place in a new tree, drawn the same way: a scan's next snapshot, or what a
            // removal left. A change of view, such as a finished scan drawn as a treemap after its
            // snapshots' icicles, is not. The volume beside it may have new figures, as a removal
            // frees space, but it is drawn beside both or beside neither.
            SamePlace = another
                && viewport is null
                && asked.View == shown.View
                && asked.Beside == shown.Beside
                && asked.Tree is not null
                && shown.Tree is ITrackedTree tracked
                && ExploreSurface.Drawn(asked.Tree, asked.View) == ExploreSurface.Drawn(tracked, shown.View)
                && tracked.CounterpartsIn(asked.Tree)?.Invoke(shown.Node) == asked.Node,

            Morph = another
                && viewport is null
                && asked.Tree is not null
                && ReferenceEquals(asked.Tree, shown.Tree)
                && asked.Node == shown.Node
                && asked.Volume == shown.Volume
                && ViewMorph.Morphs(asked.Tree, shown.View, asked.View),

            Zooms = zooms,
            Opened = zooms && viewport is { } revisited ? revisited : MapViewport.Whole,
            Glide = !another && zooms ? viewport : null,
        };
    }

    /// <summary>
    /// Whether this asks for the picture a flight on its way is leaving, <paramref name="left"/> as it
    /// was at <paramref name="leftAt"/>, so the flight can turn round where it is rather than land and
    /// fly again. Only at the zoom it was left at: one asked for anywhere else is another flight.
    /// </summary>
    public bool Returns(MapPicture left, MapViewport leftAt) => Another && Asked == left && Opened == leftAt;

    /// <summary>
    /// Which way the camera flies from <see cref="Shown"/> to <see cref="Asked"/>.
    ///
    /// <para>Only between two folders of one tree drawn in one view as a treemap the map can zoom, from
    /// a drawing that is on screen and has a camera of its own. An icicle and a sunburst have no
    /// camera, and change at once. A folder with nothing in it to draw has nothing to fly into, so a
    /// step through to one changes at once too: the folder passed through on the way would be the last
    /// thing seen.</para>
    /// </summary>
    /// <param name="drawingZooms">
    /// Whether the drawing on screen now is one placed by the zoom, which is the drawing the flight
    /// starts from. Asked once a flight already on its way has landed, which can change the drawing.
    /// </param>
    public PictureStep Step(bool drawingZooms)
    {
        if (!Zooms || !drawingZooms || !ReferenceEquals(Asked.Tree, Shown.Tree) || Asked.View != Shown.View)
        {
            return PictureStep.Across;
        }

        var tree = Asked.Tree!;
        var way = FolderSteps.Between(tree, Shown.Node, Shown.Beside, Asked.Node, Asked.Beside);

        if (way != FolderStep.Through)
        {
            return new PictureStep(way, null);
        }

        return tree.SizeOf(Asked.Node) <= 0
            ? PictureStep.Across
            : new PictureStep(FolderStep.Through, FolderSteps.Meeting(tree, Shown.Node, Asked.Node));
    }
}
