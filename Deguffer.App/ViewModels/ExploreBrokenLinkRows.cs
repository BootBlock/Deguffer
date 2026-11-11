using System.Collections.Concurrent;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Links;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Files layout's rows when it lists broken links, and the sentence under its filters. Apart from
/// <see cref="ExploreFiles"/>, which decides when to ask, because what is listed is Core's
/// (<see cref="BrokenLinks"/>) and this only turns the answer into rows and words.
/// </summary>
internal static class ExploreBrokenLinkRows
{
    /// <summary>
    /// The rows for the broken links at or below <paramref name="root"/>, and the sentence saying how
    /// many there are and how many were left out. Run off the window's thread: each link is read.
    /// </summary>
    /// <param name="built">The layout's rows for this tree, which a row of the broken links is kept in.</param>
    public static (IReadOnlyList<ExploreFileRow> Rows, string Summary) Find(
        ExploreTree tree,
        int root,
        FileFilter filter,
        DateTime now,
        LinkInspection inspection,
        ConcurrentDictionary<(int Node, bool Link), ExploreFileRow> built,
        Func<string, ExploreVerdict> verdict,
        CancellationToken ct)
    {
        var found = BrokenLinks.Find(tree, root, filter, now, inspection, LargestFiles.Limit, ct);
        var rows = new ExploreFileRow[found.Links.Count];

        for (var at = 0; at < rows.Length; at++)
        {
            ct.ThrowIfCancellationRequested();

            var link = found.Links[at];
            rows[at] = built.GetOrAdd((link.Node, true), key => ExploreFileRow.For(tree, key.Node, now, verdict, link.Link));
        }

        return (rows, SummaryOf(found, filter.IsEverything));
    }

    /// <summary>
    /// How many links lead nowhere, how many are listed, and how many were left out because where they
    /// lead cannot be reached. Those are counted rather than listed, because a link is offered only
    /// where its target is gone, and a drive that is not connected may come back.
    /// </summary>
    /// <param name="everything">Whether no filter narrows the list.</param>
    internal static string SummaryOf(BrokenLinkListing found, bool everything)
    {
        var (gone, listed) = (found.Gone, found.Links.Count);

        var sentence = (gone, found.IsComplete, everything) switch
        {
            (0, _, true) => "No shortcut or link here leads nowhere.",
            (0, _, false) => "No shortcut or link here that matches leads nowhere.",
            (1, _, _) => "1 shortcut or link here leads nowhere.",
            (var count, true, _) => $"{count:N0} shortcuts and links here lead nowhere, by path.",
            (var count, false, _) => $"The first {listed:N0} of {count:N0} shortcuts and links here that lead nowhere, by path.",
        };

        return found.Unreachable switch
        {
            0 => sentence,
            1 => $"{sentence} 1 more leads to a drive or share that is not connected or could not be checked, so it is not listed.",
            var count => $"{sentence} {count:N0} more lead to a drive or share that is not connected or could not be checked, so they are not listed.",
        };
    }
}
