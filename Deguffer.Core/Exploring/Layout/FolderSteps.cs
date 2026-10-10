namespace Deguffer.Core.Exploring.Layout;

/// <summary>Which way the map moves between two folders of one tree.</summary>
public static class FolderSteps
{
    /// <summary>
    /// Which way the map moves from <paramref name="from"/> to <paramref name="to"/> in
    /// <paramref name="tree"/>, each drawn with the volume beside it where <paramref name="fromBeside"/>
    /// or <paramref name="toBeside"/> says so.
    ///
    /// <para>The same folder with the volume beside it and then without is the root opened out of the
    /// volume, which is a step into it, and the other way round is a step out to it. Any number of
    /// levels is one step: going up three folders at once is one pull-back, not three.</para>
    /// </summary>
    public static FolderStep Between(ISizedTree tree, int from, bool fromBeside, int to, bool toBeside)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (from == to)
        {
            return (fromBeside, toBeside) switch
            {
                (true, false) => FolderStep.Into,
                (false, true) => FolderStep.OutOf,
                _ => FolderStep.Across,
            };
        }

        if (Holds(tree, from, to))
        {
            return FolderStep.Into;
        }

        return Holds(tree, to, from) ? FolderStep.OutOf : FolderStep.Across;
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="folder"/> or anywhere under it.</summary>
    private static bool Holds(ISizedTree tree, int folder, int node)
    {
        for (var current = node; ; current = tree.ParentOf(current))
        {
            if (current == folder)
            {
                return true;
            }

            if (tree.ParentOf(current) == current)
            {
                return false;
            }
        }
    }
}
