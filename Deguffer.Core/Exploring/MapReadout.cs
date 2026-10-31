using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The line under a map: what the pointer or the keyboard is on, how big it is, and what is known of
/// it. One for every page that draws a map, so a block reads alike on each and a page says only what
/// it knows of its own nodes (<see cref="IMapShapes{TTree}"/>).
/// </summary>
/// <param name="Name">What it is: a path on a drive, a program's name in memory, or a block's readout.</param>
/// <param name="Figures">
/// How big it is, and what else the page states of it, said apart from the name. The line holds one
/// line's worth and a name is what overruns it, so the figures take the width they need and the name
/// trims into what is left.
/// </param>
/// <param name="Note">What is known of it beyond the figures, for the map's tooltip; empty where nothing is.</param>
public sealed record MapReadout(string Name, string Figures, string Note)
{
    /// <summary>The line with nothing under the pointer or the keyboard.</summary>
    public static MapReadout None { get; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>Whether there is a note, which collapses the map's tooltip where there is none.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>
    /// The line where <paramref name="hit"/> is on the map of <paramref name="shown"/>: a block by
    /// its readout and its size, a node by what the page says of it, and nothing where the page has
    /// no tree or the tree no longer holds the node (§7.1).
    /// </summary>
    public static MapReadout For<TTree>(TTree? shown, ExploreHit? hit, IMapShapes<TTree> shapes)
        where TTree : class, IShownTree
    {
        ArgumentNullException.ThrowIfNull(shapes);

        return (shown, hit) switch
        {
            ({ } tree, { Block: { } block, Bytes: var bytes }) =>
                new MapReadout(block.Readout, FreeSpace.Format(bytes), shapes.NoteOn(tree, block)),

            ({ } tree, { IsNode: true, Node: var node }) when tree.Holds(node) => shapes.ReadoutOf(tree, node),

            _ => None,
        };
    }
}
