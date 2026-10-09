using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Configuration;

/// <summary>The unit a size limit on the Duplicates page is written in, each 1,024 of the one before, as File Explorer counts.</summary>
public enum SizeUnit
{
    Bytes = 0,
    KB = 1,
    MB = 2,
    GB = 3,
}

/// <summary>
/// A smallest or largest size, as the user wrote it: an amount, or none for no limit, and its unit.
///
/// <para>Stored as written rather than as bytes, so a limit of 1.5 MB reads back as 1.5 MB rather
/// than as the 1,536 KB the bytes would most simply print as. The unit is kept with no amount, so a
/// person who chooses GB and then types 5 gets 5 GB. Nothing validates <c>preferences.json</c> on the
/// way in, so an amount here can be negative or not a number, and <see cref="Bytes"/> answers for
/// every value.</para>
/// </summary>
public sealed record SizeLimit(double? Amount, SizeUnit Unit)
{
    /// <summary>No limit, written in megabytes once an amount is typed.</summary>
    public static readonly SizeLimit None = new(null, SizeUnit.MB);

    /// <summary>
    /// The amount a size box holds, as its value: a <c>NumberBox</c> gives <see cref="double.NaN"/>
    /// for an empty one, which is no limit.
    /// </summary>
    public static double? Entered(double value) => double.IsNaN(value) ? null : value;

    /// <summary>
    /// The limit in bytes, rounded to the nearest, or null where there is no amount. A negative
    /// amount stays negative, so the search refuses it with its reason
    /// (<see cref="SizeRange.WhyRefused"/>) rather than reading it as no limit, and an amount past
    /// what a file can hold reads as the most there is.
    /// </summary>
    public long? Bytes
    {
        get
        {
            if (Amount is not { } amount || double.IsNaN(amount))
            {
                return null;
            }

            var bytes = Math.Round(amount * Math.Pow(1024, (int)Unit));

            return bytes >= long.MaxValue ? long.MaxValue : bytes <= long.MinValue ? long.MinValue : (long)bytes;
        }
    }
}

/// <summary>
/// What the Duplicates page searches with (§7.4), remembered between searches: the criteria, the
/// checksum, and every filter. The locations are not among them: they are what one search is about.
///
/// <para>The defaults are §7.4's: a match on content by XXH128, every extension and every size, and
/// neither hidden nor system files, nor the places a search passes over by default.</para>
///
/// <para>Each value is stored as the user chose it, and <see cref="Search"/> turns them into the one
/// <see cref="DuplicateSearch"/> the page runs, so the page holds no copy of how a filter is read.</para>
/// </summary>
public sealed record DuplicatePreferences
{
    public static readonly DuplicatePreferences Default = new();

    public MatchCriteria Criteria { get; init; } = MatchCriteria.Content;

    public ChecksumAlgorithm Algorithm { get; init; } = ChecksumAlgorithm.XxHash128;

    public SizeLimit Smallest { get; init; } = SizeLimit.None;

    public SizeLimit Largest { get; init; } = SizeLimit.None;

    public ExtensionFilterMode ExtensionMode { get; init; } = ExtensionFilterMode.Any;

    /// <summary>The extensions as the user typed them, separated by commas, semicolons or spaces.</summary>
    public string Extensions { get; init; } = string.Empty;

    public bool SearchHidden { get; init; }

    public bool SearchSystem { get; init; }

    /// <summary>Whether the places a search passes over by default are searched (<see cref="DuplicateSearch.SearchPassedOverPlaces"/>).</summary>
    public bool SearchPassedOverPlaces { get; init; }

    /// <summary>
    /// The checksum to search with: the one chosen where this machine offers it, and otherwise the
    /// default. A file written on a machine with SHA3-256 can be read on one without it, and a
    /// hand-edited number can name no checksum at all.
    /// </summary>
    public ChecksumAlgorithm OfferedAlgorithm =>
        ChecksumAlgorithms.IsOffered(Algorithm) ? Algorithm : ChecksumAlgorithm.XxHash128;

    /// <summary>The sizes a file may have to be searched.</summary>
    public SizeRange Sizes => new(Smallest.Bytes, Largest.Bytes);

    /// <summary>The extension filter, read from what the user typed.</summary>
    public ExtensionFilter ExtensionFilter => ExtensionFilter.Parse(ExtensionMode, Extensions);

    /// <summary>Why these preferences cannot search <paramref name="locations"/>, or null where they can.</summary>
    public string? WhyRefused(IReadOnlyCollection<SearchLocation> locations) =>
        DuplicateSearch.WhyRefused(Criteria, locations, Sizes, ExtensionFilter);

    /// <summary>The search of <paramref name="locations"/> with these preferences.</summary>
    /// <exception cref="ArgumentException"><see cref="WhyRefused"/> gives a reason.</exception>
    public DuplicateSearch Search(IReadOnlyList<SearchLocation> locations) =>
        new(Criteria, locations, OfferedAlgorithm, Sizes, ExtensionFilter, SearchHidden, SearchSystem, SearchPassedOverPlaces);
}
