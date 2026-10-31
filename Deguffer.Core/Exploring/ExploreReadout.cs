using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the line under Explore's map says of a scanned drive: of one of its nodes, and of the blocks
/// that stand for the rest of the volume. The rest of the line is <see cref="MapReadout"/>'s, the
/// same on every page.
/// </summary>
public static class ExploreReadout
{
    /// <summary>
    /// The three things the line says of one shape: where it is, how big and how old it is, and what
    /// is known of it.
    ///
    /// <para>The age is here whichever colouring is on, not only when the map is drawn by age. A
    /// pointer is how somebody checks one shape against the rest, and having to change the
    /// colouring to read a date would be a worse answer than showing it always.</para>
    ///
    /// <para>The path is built once and the reference is asked with it, rather than each walking
    /// the tree's parent chain for itself. This runs on every move that lands on a different shape
    /// (G4).</para>
    /// </summary>
    public static MapReadout Over(ExploreTree tree, int node, in ExploreReadoutState state)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var path = tree.PathOf(node);
        var storage = ExploreNodeText.Storage(tree, node);
        var figures = $"{FreeSpace.Format(tree.SizeOf(node))} on disk, "
            + (storage.Length > 0 ? $"{storage}, " : string.Empty)
            + $"last written {ExploreNodeText.Age(tree, node, state.Now)}";

        // The shape's own change, where the map is coloured by growth: compared, or settled by its
        // dates. A shape painted by the folder above it has none of its own, and the line says
        // nothing rather than borrow that one.
        if (state.Colouring == ExploreColouring.Growth
            && state.Growth is { } growth && growth.Describes(tree)
            && growth.ChangeOf(node) is { } change)
        {
            figures += $", {GrowthText.Change(change, growth.Earlier.UnrecordedAtMost)} since the last scan";
        }

        // The kind the shape is painted, where the map is coloured by type, so the colour is never the
        // only thing saying it (§6.5). A folder not measured yet says nothing rather than guess.
        if (state.Colouring == ExploreColouring.Type && DominantTypes.KindOf(tree, node, state.Dominant) is { } category)
        {
            var kind = FileCategories.Label(category).ToLowerInvariant();

            figures += tree.IsDirectory(node) ? $", largest kind: {kind}" : $", {kind}";
        }

        // The level the shape is painted for, where the map is coloured by depth, for the same reason.
        if (state.Colouring == ExploreColouring.Depth && DepthPalette.LevelOf(tree, node, state.Current) is { } level)
        {
            figures += $", {DepthPalette.Name(level).ToLowerInvariant()}";
        }

        return new MapReadout(
            path,
            figures,
            Joined(state.Guide.DescribeNearest(path)?.Tip(), HiddenSpaceNote.For(tree, node, state.Volume)));
    }

    /// <summary>
    /// Why the space <paramref name="block"/> stands for is there, where the page can say: what the
    /// scan could not count, and what Windows states of the space it keeps. Empty for the rest.
    /// </summary>
    public static string NoteOn(ExploreTree tree, MapBlock block, in ExploreReadoutState state)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(block);

        return block == MapBlock.Unaccounted ? ExploreUnaccountedNote.For(state.IsElevated, state.Volume, tree.TotalBytes, state.Strategy)
            : block == MapBlock.ShadowCopies ? HiddenSpaceNote.ShadowCopies(state.Volume.Hidden.ShadowCopies)
            : block == MapBlock.ReservedStorage ? HiddenSpaceNote.ReservedStorage()
            : string.Empty;
    }

    /// <summary>
    /// What the reference says about a shape, then what Windows states about it, either of which may
    /// be absent.
    /// </summary>
    private static string Joined(string? described, string stated) =>
        string.Join("\n\n", new[] { described, stated }.Where(part => !string.IsNullOrEmpty(part)));
}
