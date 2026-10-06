namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// The largest files under one node of one tree that pass one filter, largest first, and how many
/// passed it in all. What <see cref="LargestFiles.Find"/> answers.
///
/// <para>It carries the question it answers along with the answer, because the next question is
/// compared with it: see <see cref="LargestFiles"/> for when this can be filtered rather than found
/// again.</para>
/// </summary>
public sealed class FileRanking
{
    internal FileRanking(
        ExploreTree tree, int root, FileFilter filter, DateTime? cutoff, int limit, IReadOnlyList<int> files, int matched)
    {
        Tree = tree;
        Root = root;
        Filter = filter;
        Cutoff = cutoff;
        Limit = limit;
        Files = files;
        Matched = matched;
    }

    public ExploreTree Tree { get; }

    /// <summary>The node every file listed is at or below.</summary>
    public int Root { get; }

    public FileFilter Filter { get; }

    /// <summary>The newest write a listed file may have, or null where the filter has no age.</summary>
    public DateTime? Cutoff { get; }

    /// <summary>The most files this lists.</summary>
    public int Limit { get; }

    /// <summary>
    /// The files, by node, largest first. Two files of one size are in node order, so the same tree
    /// lists them the same way every time.
    /// </summary>
    public IReadOnlyList<int> Files { get; }

    /// <summary>How many files passed the filter, of which <see cref="Files"/> are the largest.</summary>
    public int Matched { get; }

    /// <summary>Whether <see cref="Files"/> is every file that passed, rather than the largest of them.</summary>
    public bool IsComplete => Matched == Files.Count;
}
