namespace Deguffer.Core.Duplicates;

/// <summary>
/// One duplicate search as the user asked for it (§7.4): what makes two files a match, where to
/// look and in what role, and which files to leave out.
///
/// <para><b>Refused at construction</b> where it could find nothing or could not be run: no
/// criterion, no location, a criterion no member names, a size range that admits no file, or an
/// extension filter that lists something other than extensions or, to search, lists none. A
/// search that cannot exist cannot be started, so nothing downstream has to ask again.
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
