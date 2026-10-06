using System.IO.Enumeration;

namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// Which files the Files layout lists. Every criterion is applied together, and one left at its
/// default lets everything through.
///
/// <para>A value, so that two queries can be compared: <see cref="LargestFiles"/> reuses an earlier
/// result where this one can only narrow it.</para>
/// </summary>
/// <param name="Name">
/// Text the name must contain, or a wildcard the whole name must match where it holds <c>*</c> or
/// <c>?</c> — <c>*.iso</c>. Case does not matter. Null or blank lets every name through.
/// </param>
/// <param name="Category">The kind of file to list, or null for every kind. See <see cref="FileCategories"/>.</param>
/// <param name="MinimumBytes">The smallest file to list. Zero lists every size.</param>
/// <param name="UnwrittenFor">How long a file must have gone without a write. See <see cref="FileAge"/>.</param>
public sealed record FileFilter(
    string? Name = null,
    FileCategory? Category = null,
    long MinimumBytes = 0,
    FileAge UnwrittenFor = FileAge.Any)
{
    /// <summary>The filter that lists every file.</summary>
    public static FileFilter Everything { get; } = new();

    /// <summary>Whether every criterion is at its default, so that every file passes.</summary>
    public bool IsEverything =>
        Pattern is null && Category is null && MinimumBytes <= 0 && UnwrittenFor == FileAge.Any;

    /// <summary>The name criterion with the surrounding space taken off, or null where there is none.</summary>
    private string? Pattern => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    /// <summary>
    /// Whether file <paramref name="node"/> of <paramref name="tree"/> passes every criterion.
    ///
    /// <para>A file with no known write date never passes an age criterion. Nothing shows it has
    /// gone a year untouched, and listing it under that heading would state something the scan
    /// never measured.</para>
    /// </summary>
    /// <param name="cutoff">What <see cref="FileAges.Cutoff"/> gave for <see cref="UnwrittenFor"/>.</param>
    public bool Matches(ExploreTree tree, int node, DateTime? cutoff)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (tree.SizeOf(node) < MinimumBytes)
        {
            return false;
        }

        if (cutoff is { } before && !(tree.ModifiedOf(node).Utc is { } written && written <= before))
        {
            return false;
        }

        var name = tree.NameOf(node);

        return (Category is not { } category || FileCategories.Of(name) == category)
            && (Pattern is not { } pattern || NameMatches(pattern, name));
    }

    /// <summary>
    /// Whether every file this lets through, <paramref name="wider"/> lets through too, so its result
    /// can be filtered rather than found again.
    ///
    /// <para>Criterion by criterion, and conservative: two name patterns are only known to nest
    /// where they are the same text, so anything else in the name box answers false.</para>
    /// </summary>
    /// <param name="cutoff">This filter's cutoff, from <see cref="FileAges.Cutoff"/>.</param>
    /// <param name="widerCutoff">The cutoff <paramref name="wider"/> was applied with.</param>
    public bool Narrows(FileFilter wider, DateTime? cutoff, DateTime? widerCutoff)
    {
        ArgumentNullException.ThrowIfNull(wider);

        return (wider.Pattern is null || string.Equals(wider.Pattern, Pattern, StringComparison.OrdinalIgnoreCase))
            && (wider.Category is null || wider.Category == Category)
            && MinimumBytes >= wider.MinimumBytes
            && (widerCutoff is null || cutoff <= widerCutoff);
    }

    /// <summary>
    /// Whether this differs from <paramref name="other"/> in nothing but a minimum at least as high.
    /// See <see cref="LargestFiles"/> for why that one change can reuse even a truncated result.
    /// </summary>
    public bool OnlyRaisesMinimumOf(FileFilter other, DateTime? cutoff, DateTime? otherCutoff)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Pattern, other.Pattern, StringComparison.OrdinalIgnoreCase)
            && Category == other.Category
            && cutoff == otherCutoff
            && MinimumBytes >= other.MinimumBytes;
    }

    /// <summary>
    /// A wildcard where the text holds one, matched against the whole name as Explorer's search box
    /// matches it, and otherwise text the name has to contain.
    /// </summary>
    private static bool NameMatches(string pattern, string name) =>
        pattern.AsSpan().ContainsAny('*', '?')
            ? FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)
            : name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}
