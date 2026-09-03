namespace Deguffer.App.ViewModels;

/// <summary>One row of the list view, and one entry of any other view's detail.</summary>
/// <param name="Node">Which node in the tree this stands for.</param>
/// <param name="Share">
/// How much of the containing directory this accounts for, 0 to 100. The bar is drawn against the
/// parent rather than against the largest sibling, because the question the list answers is "what
/// is this folder made of" and a bar scaled to the biggest child says nothing about that.
/// </param>
public sealed record ExploreRow(
    int Node,
    string Name,
    string SizeLabel,
    double Share,
    bool IsDirectory,
    bool IsLink,
    bool IsApproximate)
{
    /// <summary>
    /// A folder, a link or a file. Segoe Fluent Icons, and never the only thing carrying the
    /// distinction — the row is named for a screen reader by <see cref="Description"/>, which says
    /// it in words.
    /// </summary>
    public string Icon => (IsLink, IsDirectory) switch
    {
        // Written as escapes rather than as the characters themselves: these are private-use
        // codepoints, so pasted into a source file they are indistinguishable from each other, and
        // from nothing at all, in most tooling.
        (true, _) => "\uE71B",   // Link
        (_, true) => "\uE8B7",   // Folder
        _ => "\uE7C3",           // Page
    };

    /// <summary>What a screen reader should say instead of reading a bar graphic.</summary>
    public string Description =>
        $"{Name}, {SizeLabel}{(IsLink ? ", a link" : string.Empty)}"
        + $"{(IsApproximate ? ", at least" : string.Empty)}";
}

/// <summary>One step of the path back to the root.</summary>
public sealed record ExploreCrumb(int Node, string Name);
