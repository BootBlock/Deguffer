using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Finds nodes of one scanned tree in another scan of the same place, by path: the root is the root
/// where both are of the same path, and any other node is the child of its parent's counterpart
/// with the same name, without regard to case as Windows names are. See
/// <see cref="ITrackedTree.CounterpartsIn"/>.
///
/// <para>The common case costs nothing: within one walked scan a node keeps its number from one
/// snapshot to the next, so a node standing under its parent's counterpart with the same name is
/// taken as it is. Otherwise the parent's children are indexed by name once, and every later lookup
/// in that folder is a dictionary read, because a folder of a hundred thousand files would make a
/// search per node the slowest step of all.</para>
/// </summary>
internal sealed class ExploreCounterparts(ExploreTree earlier, ExploreTree later)
{
    private readonly Dictionary<int, int?> _found = new() { [earlier.RootNode] = later.RootNode };

    private readonly Dictionary<int, ChildrenByName> _names = [];

    public int? Find(int node)
    {
        if (_found.TryGetValue(node, out var known))
        {
            return known;
        }

        var parent = earlier.ParentOf(node);

        // A node with no parent other than itself and not the root is one the tree cannot place.
        var found = parent != node && Find(parent) is { } there ? Child(there, node) : null;

        _found[node] = found;

        return found;
    }

    private int? Child(int folder, int number)
    {
        var name = earlier.NameSpanOf(number);

        // Held, because a node taken out of the later tree since its scan keeps its number and its
        // parent there, and is no counterpart of anything (ExploreTree.Without).
        if (later.Holds(number)
            && number != later.RootNode
            && later.ParentOf(number) == folder
            && later.NameSpanOf(number).Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return number;
        }

        if (!_names.TryGetValue(folder, out var children))
        {
            children = new ChildrenByName(later);

            // The first of two names that differ only in case keeps it, as a directory with case
            // sensitivity turned on can hold both; the order is the tree's own, so it is the same
            // answer every time.
            foreach (var child in later.ChildrenOf(folder))
            {
                children.Add(child);
            }

            _names[folder] = children;
        }

        return children.Find(name);
    }
}
