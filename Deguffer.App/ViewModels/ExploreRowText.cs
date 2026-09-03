using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// How a node of a scanned tree is worded: its size, its age, and its two dates in full.
///
/// <para>Separate from <see cref="ExploreViewModel"/> because it is a different job. That one
/// decides which node is being looked at; this decides what a reader is told about one, and every
/// rule here is about honesty rather than about navigation — where a figure is a bound rather than
/// a measurement, and in which direction it can be wrong (G1).</para>
///
/// <para>Static and in the shell rather than in Core, unlike <see cref="FreeSpace"/> and
/// <see cref="RelativeAge"/>, which it composes. Those two are rules about what any part of the app
/// may say; these are this page's own columns, and no other page has them.</para>
/// </summary>
internal static class ExploreRowText
{
    /// <summary>
    /// A row's size, marked where it is a lower bound.
    ///
    /// <para>A directory the walk was refused totals only what it could see, and the plain figure
    /// reads as a measurement. The page says so once in its status line, which is true and is not
    /// enough — it is the row for <c>System Volume Information</c> showing "0 B" that a reader acts
    /// on. <see cref="ExploreRow.Description"/> says the same in words for a screen reader, because
    /// a symbol read aloud is not a sentence.</para>
    /// </summary>
    public static string Size(ExploreTree tree, int node) =>
        tree.HasUnknownSizeBelow(node)
            ? "≥ " + FreeSpace.Format(tree.SizeOf(node))
            : FreeSpace.Format(tree.SizeOf(node));

    /// <summary>
    /// A row's age, marked where the scan could not see under it.
    ///
    /// <para>A directory the walk was refused contributes only its own timestamp to the roll-up,
    /// and a directory's own timestamp moves when its layout changes rather than when its contents
    /// do — so a log being appended to right now behind a refused listing reads as years idle.
    /// Core's <c>DirectoryAge</c> answers "unknown" in that position rather than risk it, which is
    /// the right trade for a row that prices a deletion and the wrong one here: nearly every drive
    /// has some corner it cannot read, so blanking the column would blank the drive root and every
    /// folder above the refusal.</para>
    ///
    /// <para>So the date is kept and qualified, in the direction the error actually runs. The age
    /// shown can only be too old, never too new — the same asymmetry <see cref="Size"/> marks with
    /// "≥", and stated in words rather than a symbol because a screen reader reads this one
    /// aloud.</para>
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read, so one list is dated against one instant and two rows a
    /// millisecond apart cannot land in different days.
    /// </param>
    public static string Age(ExploreTree tree, int node, DateTime now)
    {
        var written = tree.ModifiedOf(node);
        var age = RelativeAge.Describe(written.Utc, now);

        return written.IsKnown && tree.HasUnknownSizeBelow(node) ? $"{age} or newer" : age;
    }

    /// <summary>
    /// Both of a row's dates in full, for its tooltip.
    ///
    /// <para>Local time and the user's own format, because this is the one place the exact instant
    /// is shown and a reader compares it against what Explorer says beside it. Everything inside the
    /// tree is UTC, which is what makes two scan routes agree; the conversion belongs here, at the
    /// last moment before a person reads it.</para>
    ///
    /// <para>The two lines say different things and the labels have to keep them apart. Created is
    /// the node's own date. The other is the newest write anywhere at or below it, so for a folder
    /// it is not the folder's own timestamp — see <see cref="ExploreTree.ModifiedOf"/> — and calling
    /// it "modified" without saying so invites the reader to compare it with a figure Explorer shows
    /// for something else.</para>
    /// </summary>
    public static string Dates(ExploreTree tree, int node)
    {
        var created = tree.CreatedOf(node).Utc;
        var modified = tree.ModifiedOf(node).Utc;

        var newest = tree.IsDirectory(node) ? "Newest write inside" : "Last written";

        return $"Created: {Exact(created)}{Environment.NewLine}{newest}: {Exact(modified)}";
    }

    private static string Exact(DateTime? when) =>
        when is { } value ? value.ToLocalTime().ToString("g") : "not known";
}
