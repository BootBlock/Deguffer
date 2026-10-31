using Deguffer.Core.Exploring;

namespace Deguffer.App.ViewModels;

/// <summary>
/// How a row's tooltip is worded: its dates in full, and the space on disk set beside them.
///
/// <para>In the shell, unlike the size, the storage and the age, which are
/// <see cref="ExploreNodeText"/>'s: those are rules about what the page may say of a node anywhere,
/// and these are this page's own tooltip, in the reader's own time zone and format.</para>
/// </summary>
internal static class ExploreRowText
{
    /// <summary>
    /// What a row's tooltip says before anything else about it: the space on disk with the length
    /// and its explanation where there is one, then the two dates.
    /// </summary>
    public static string Details(string size, string storage, string dates) =>
        storage.Length > 0 ? $"{size} on disk, {storage}{Environment.NewLine}{dates}" : dates;

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
