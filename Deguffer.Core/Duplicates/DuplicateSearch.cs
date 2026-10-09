namespace Deguffer.Core.Duplicates;

/// <summary>
/// One duplicate search as the user asked for it (§7.4): what makes two files a match, where to
/// look and in what role, and which files to leave out.
///
/// <para><b>Refused at construction</b> where it could find nothing or could not be run: no
/// criterion, no location, a criterion or a checksum no member names, a size range that admits no
/// file, or an extension filter that lists something other than extensions or, to search, lists
/// none. A search that cannot exist cannot be started, so nothing downstream has to ask again.
/// <see cref="WhyRefused"/> gives the page the same reason before it builds one.</para>
/// </summary>
public sealed record DuplicateSearch
{
    public DuplicateSearch(
        MatchCriteria criteria,
        IReadOnlyList<SearchLocation> locations,
        ChecksumAlgorithm algorithm = ChecksumAlgorithm.XxHash128,
        SizeRange sizes = default,
        ExtensionFilter? extensions = null,
        bool searchHidden = false,
        bool searchSystem = false,
        bool searchPassedOverPlaces = false)
    {
        if (WhyRefused(criteria, locations, sizes, extensions) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(criteria));
        }

        // Not among WhyRefused's sentences: the page offers only the algorithms there are, so a
        // value no member names is a fault in the caller rather than a choice to explain. Whether
        // this machine offers a named one is asked when the search runs, not of the value.
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "No checksum has that name.");
        }

        Criteria = criteria;
        Locations = [.. locations];
        Algorithm = algorithm;
        Sizes = sizes;
        Extensions = extensions ?? ExtensionFilter.Any;
        SearchHidden = searchHidden;
        SearchSystem = searchSystem;
        SearchPassedOverPlaces = searchPassedOverPlaces;
    }

    public MatchCriteria Criteria { get; }

    public IReadOnlyList<SearchLocation> Locations { get; }

    /// <summary>The checksum a content match groups by. Read only where <see cref="Criteria"/> holds the content.</summary>
    public ChecksumAlgorithm Algorithm { get; }

    public SizeRange Sizes { get; }

    public ExtensionFilter Extensions { get; }

    /// <summary>Whether a file carrying the hidden attribute is searched. Off by default (§7.4).</summary>
    public bool SearchHidden { get; }

    /// <summary>Whether a file carrying the system attribute is searched. Off by default (§7.4).</summary>
    public bool SearchSystem { get; }

    /// <summary>
    /// Whether the places a search passes over by default are searched: what Explore refuses at and
    /// below, and program folders. A copy in one is still matched, and it is refused when marking.
    /// </summary>
    public bool SearchPassedOverPlaces { get; }

    /// <summary>Why a search with these values cannot be run, or null where it can.</summary>
    public static string? WhyRefused(
        MatchCriteria criteria,
        IReadOnlyCollection<SearchLocation> locations,
        SizeRange sizes = default,
        ExtensionFilter? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(locations);

        if (criteria == MatchCriteria.None)
        {
            return "Choose at least one thing two files must share to be a match.";
        }

        if ((criteria & ~MatchCriteriaRules.All) != 0)
        {
            return "The search names a way of matching files that Deguffer does not have.";
        }

        if (locations.Count == 0)
        {
            return "Choose at least one drive or folder to search.";
        }

        return sizes.WhyRefused ?? extensions?.WhyRefused;
    }

    /// <summary>
    /// Why what this search found no longer answers for the locations <paramref name="chosen"/> now,
    /// or null where they are the ones it searched, in the same roles, in any order.
    ///
    /// <para>A file's role is decided as the search finds it, by the innermost location holding it, so
    /// a location added, taken away or given another role after the search leaves its groups and
    /// marks in roles the user no longer chose: a folder made a reference would still offer its
    /// copies for removal. Paths compare exactly, as the list does, since a case-sensitive folder can
    /// hold two that differ only in case.</para>
    /// </summary>
    public string? WhyResultsDoNotApply(IReadOnlyCollection<SearchLocation> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        return chosen.Count == Locations.Count && Locations.ToHashSet().SetEquals(chosen)
            ? null
            : "The locations or their roles changed after this search, so its groups show copies in the roles they had then. "
              + "Search again to mark or remove copies.";
    }

    public bool Equals(DuplicateSearch? other) =>
        other is not null
        && Criteria == other.Criteria
        && Locations.SequenceEqual(other.Locations)
        && Algorithm == other.Algorithm
        && Sizes == other.Sizes
        && Extensions.Equals(other.Extensions)
        && SearchHidden == other.SearchHidden
        && SearchSystem == other.SearchSystem
        && SearchPassedOverPlaces == other.SearchPassedOverPlaces;

    public override int GetHashCode() => HashCode.Combine(Criteria, Locations.Count, Algorithm, Sizes);
}
