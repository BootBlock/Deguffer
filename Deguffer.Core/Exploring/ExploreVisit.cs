using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring;

/// <summary>
/// One place the reader stood in a scanned tree, and how far into its picture they had zoomed there.
/// A step on <see cref="ExploreVisits"/>, which goes back and forward over them.
///
/// <para>The zoom is part of the place because zooming to a shape is a step of its own: a
/// double-click on a file zooms to it rather than opening it, and going back has to undo that as it
/// undoes opening a folder. It is the zoom on the treemap, and the whole picture on any view that
/// does not zoom.</para>
/// </summary>
/// <param name="Position">Which node the views were on.</param>
/// <param name="Viewport">The part of the picture on screen there.</param>
public readonly record struct ExploreVisit(ExplorePosition Position, MapViewport Viewport)
{
    /// <summary>
    /// Whether this shows what <paramref name="other"/> shows in <paramref name="tree"/> beside
    /// <paramref name="volume"/>: the same picture, by <see cref="ExplorePosition.Shows"/>, at the
    /// same zoom where the view on screen <paramref name="zooms"/>, and at any zoom where it does
    /// not, because that view shows no zoom at all.
    /// </summary>
    public bool Shows(ExploreVisit other, ExploreTree tree, VolumeSpace volume, bool zooms) =>
        Position.Shows(other.Position, tree, volume) && (!zooms || Viewport == other.Viewport);
}
