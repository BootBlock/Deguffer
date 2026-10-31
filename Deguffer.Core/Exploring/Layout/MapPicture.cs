using System.Runtime.CompilerServices;
using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which picture the map draws: <see cref="Node"/> of <see cref="Tree"/>, in <see cref="View"/>,
/// with <see cref="Volume"/> beside it or <see cref="VolumeSpace.None"/>. A new colouring, scheme,
/// spacing or finish draws the same picture differently, so none of them is part of it.
///
/// <para>Two pictures are the same only of the same tree object. A scan's next snapshot, or what a
/// removal left, is a new tree of the same place, and another picture of it (see
/// <see cref="PictureChange.SamePlace"/>), whatever a tree may one day say about its own equality.</para>
/// </summary>
/// <param name="Tree">The tree drawn, or null for a map with nothing to draw.</param>
/// <param name="Node">The node of <paramref name="Tree"/> the picture is of.</param>
/// <param name="View">The view asked for, which may not be the one drawn (<see cref="Rendering.ExploreSurface.Drawn"/>).</param>
/// <param name="Volume">The volume drawn beside <paramref name="Node"/>, or <see cref="VolumeSpace.None"/>.</param>
public readonly record struct MapPicture(ISizedTree? Tree, int Node, ExploreView View, VolumeSpace Volume)
{
    /// <summary>Whether the volume is drawn beside the node: the root of a whole volume, before it is opened.</summary>
    public bool Beside => Volume != VolumeSpace.None;

    public bool Equals(MapPicture other) =>
        ReferenceEquals(Tree, other.Tree) && Node == other.Node && View == other.View && Volume == other.Volume;

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Tree), Node, View, Volume);
}
