using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row of the Files layout: a file at any depth below the folder on screen, where it is, how big
/// and how old, and why Explore will not remove it where it will not.
///
/// <para>A value rather than a row written over in place, as <see cref="ExploreRow"/> is. Nothing on
/// it is edited while it is on screen: a new answer arrives as a new list, and
/// <see cref="Core.Viewing.LiveList"/> replaces only the rows that changed.</para>
/// </summary>
/// <param name="Path">The file's full path, which is also what tells it apart from a namesake.</param>
/// <param name="Folder">The folder it is in, in full: the one fact a flat list has to add to a name.</param>
/// <param name="Refusal">
/// Why Explore will not remove it, or null where nothing stands in the way. Stated on the row rather
/// than by leaving it out (§7.1): a list that dropped what it would not act on would be ordered by
/// removability after all.
/// </param>
public sealed record ExploreFileRow(
    int Node,
    string Path,
    string Name,
    string Folder,
    string SizeLabel,
    string AgeLabel,
    string DatesLabel,
    string? Refusal) : IExploreListed
{
    public bool IsDirectory => false;

    public bool IsRefused => Refusal is not null;

    /// <summary>The page glyph from Segoe Fluent Icons, by code point for the reason <see cref="ExploreRow.Icon"/> gives.</summary>
    public string Icon => "\uE7C3";

    /// <summary>What the row identifies, for matching it against the rows already on screen.</summary>
    public (int Node, string Path) Key => (Node, Path);

    /// <summary>The refusal as the row states it, under the folder, or null where there is none.</summary>
    public string? RefusalLine => Refusal is null ? null : $"Explore will not remove this. {Refusal}";

    /// <summary>The two dates in full, then the refusal in full where there is one.</summary>
    public string Tip => Refusal is null ? DatesLabel : $"{DatesLabel}{Environment.NewLine}{Environment.NewLine}{Refusal}";

    /// <summary>
    /// What a screen reader says for the row, the refusal included. A row is announced by this name
    /// rather than by its parts, and a tooltip is not an accessible surface here (see the list's
    /// template).
    /// </summary>
    public string Description =>
        $"{Name}, {SizeLabel}, in {Folder}, last written {AgeLabel}"
        + (Refusal is null ? string.Empty : $". Explore will not remove this: {Refusal}");

    /// <summary>
    /// The row for file <paramref name="node"/> of <paramref name="tree"/>.
    ///
    /// <para>Built off the window's thread, a thousand at a time, so everything it needs is read
    /// here: two walks up the tree for the paths and one policy question.</para>
    /// </summary>
    /// <param name="now">The instant every age in one list is measured from.</param>
    /// <param name="verdict">What Explore would say about removing a path. See <see cref="ExploreActions.Verdict"/>.</param>
    public static ExploreFileRow For(ExploreTree tree, int node, DateTime now, Func<string, ExploreVerdict> verdict)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(verdict);

        var path = tree.PathOf(node);
        var answer = verdict(path);

        return new ExploreFileRow(
            node,
            path,
            tree.NameOf(node),
            tree.PathOf(tree.ParentOf(node)),
            ExploreRowText.Size(tree, node),
            ExploreRowText.Age(tree, node, now),
            ExploreRowText.Dates(tree, node),
            answer.IsAllowed ? null : answer.Reason);
    }
}
