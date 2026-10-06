namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// How long a file must have gone without a write to be listed: "large and untouched for a year".
///
/// <para>By the last <em>write</em>, because that is the one date the tree holds for a file — see
/// <see cref="ExploreTree.ModifiedOf"/>. Last access would answer the question more directly, and
/// Windows stops keeping it up to date by default, so a filter on it would describe a setting rather
/// than the file.</para>
/// </summary>
public enum FileAge
{
    /// <summary>Written at any time, including a file with no date at all.</summary>
    Any = 0,
    ThreeMonths = 1,
    OneYear = 2,
    TwoYears = 3,
}
