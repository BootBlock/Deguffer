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
    ///
    /// <para>Between folders where neither holds the other, such as two sides of the drive opened one
    /// after the other, the step goes through the folder that holds both, so the reader sees where the
    /// one is from the other rather than a picture replaced by another.</para>
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

        return Holds(tree, to, from) ? FolderStep.OutOf : FolderStep.Through;
    }

    /// <summary>
    /// The nearest folder that holds both <paramref name="first"/> and <paramref name="second"/>:
    /// the one a <see cref="FolderStep.Through"/> passes through. One of the two where it holds the
    /// other, and the root at the furthest.
    /// </summary>
    public static int Meeting(ISizedTree tree, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(tree);

        for (var folder = first; ; folder = tree.ParentOf(folder))
        {
            if (Holds(tree, folder, second))
            {
                return folder;
            }
        }
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
