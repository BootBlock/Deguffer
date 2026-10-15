namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Finds directories by name below one directory of a volume's table, for
/// <see cref="MftVolumeIndex.TryFindDirectories"/>.
///
/// <para>Apart from <see cref="MftVolumeIndex"/> for the reason <see cref="MftLeftOutPlacement"/>
/// is: it answers a different question. The index totals a subtree it has found, and this descends
/// one by the rules of a <see cref="DirectorySearch"/>. Built for one search and dropped with it.</para>
/// </summary>
/// <param name="resolve">How the index finds the directory at a path below the volume root.</param>
internal sealed class MftDirectoryFinder(
    MftVolumeTree tree,
    MftChildLinks links,
    Func<IReadOnlyList<string>, uint?> resolve)
{
    /// <inheritdoc cref="MftVolumeIndex.TryFindDirectories"/>
    public IReadOnlyList<IReadOnlyList<string>>? TryFind(
        IReadOnlyList<string> root,
        DirectorySearch search,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        if (resolve(root) is not { } start)
        {
            return null;
        }

        // Resolved as the search of its own resolves its root, so the record handed off is the record
        // that search starts from. One reached through a link resolves to nothing, and this pass
        // never descends through a link to reach it anyway.
        var handedOff = search.HandedOff
            .Select(belowRoot => resolve([.. root, .. belowRoot]))
            .OfType<uint>()
            .ToHashSet();

        var found = new List<IReadOnlyList<string>>();

        // Each directory entered records its name and the position of the one holding it, which is
        // how a found directory's path is read back without recursion or a parent walk.
        var entered = new List<(int Holder, string Name)>();
        var stack = new Stack<(uint Node, int Holder)>();
        var visited = 0;

        Push(start, -1);

        while (stack.TryPop(out var item))
        {
            // A source folder can hold millions of entries, so the cancellation check is amortised
            // rather than paid per entry.
            if ((++visited & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var node = item.Node;

            if (tree.IsReparsePoint[node] || tree.Names[node] is not { } name)
            {
                continue;
            }

            switch (search.Meet(name))
            {
                case SearchStep.Found:
                    found.Add(PathBelowRoot(item.Holder, name));
                    break;

                case SearchStep.Entered when !handedOff.Contains(node):
                    entered.Add((item.Holder, name));
                    Push(node, entered.Count - 1);
                    break;
            }
        }

        return found;

        void Push(uint directory, int holder)
        {
            for (var i = links.Start[directory]; i < links.Start[directory + 1]; i++)
            {
                stack.Push((links.Children[i], holder));
            }
        }

        IReadOnlyList<string> PathBelowRoot(int holder, string name)
        {
            var components = new List<string> { name };

            for (var position = holder; position >= 0; position = entered[position].Holder)
            {
                components.Add(entered[position].Name);
            }

            components.Reverse();
            return components;
        }
    }
}
