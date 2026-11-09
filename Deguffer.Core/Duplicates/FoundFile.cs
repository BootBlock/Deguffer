using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// What every file found under one searched location shares, held once for the location. Compared
/// by reference, so finding a file by its place never hashes the volume's strings.
/// </summary>
/// <param name="route">The route that identifies the files of the volume they were found on.</param>
/// <param name="volume">
/// The volume they were found on: the location's, as the search resolved it, because the enumeration
/// of a location never crosses into a volume mounted in one of its folders.
/// </param>
/// <param name="folder">Where the location really is, in display form, as the search resolved it.</param>
internal sealed class FoundRoot(ExploreTree tree, IdentityRoute route, LocalVolume volume, string folder)
{
    public ExploreTree Tree { get; } = tree;

    public IdentityRoute Route { get; } = route;

    public LocalVolume Volume { get; } = volume;

    public string Folder { get; } = folder;
}

/// <summary>
/// A file a walk kept, by its place in the tree, so no path is built until it has a match. A drive
/// holds millions, and every copy of one is made in this size, so anything the files of a location
/// share belongs in <see cref="FoundRoot"/>.
/// </summary>
internal readonly record struct FoundFile(FoundRoot Root, int Node, LocationRole Role)
{
    public ExploreTree Tree => Root.Tree;

    public IdentityRoute Route => Root.Route;

    public LocalVolume Volume => Root.Volume;
}
