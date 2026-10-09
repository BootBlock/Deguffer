using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Tests;

/// <summary>
/// A duplicate search that could find nothing, or could not be run, is refused before it exists
/// (§7.4), so nothing downstream has to ask again.
/// </summary>
public sealed class DuplicateSearchTests
{
    private static readonly SearchLocation[] OneLocation = [new(@"C:\Users\testuser\Pictures")];

    [Fact]
    public void ASearchWithNoCriterionIsRefused()
    {
        Assert.NotNull(DuplicateSearch.WhyRefused(MatchCriteria.None, OneLocation));
        Assert.Throws<ArgumentException>(() => new DuplicateSearch(MatchCriteria.None, OneLocation));
    }

    [Fact]
    public void ASearchWithNoLocationIsRefused()
    {
        Assert.NotNull(DuplicateSearch.WhyRefused(MatchCriteria.Content, []));
        Assert.Throws<ArgumentException>(() => new DuplicateSearch(MatchCriteria.Content, []));
    }

    /// <summary>A stored preference written by a later version could name a criterion this one lacks.</summary>
    [Fact]
    public void ASearchNamingACriterionNoMemberNamesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new DuplicateSearch((MatchCriteria)16 | MatchCriteria.Size, OneLocation));
    }

    /// <summary>
    /// The same for a checksum: a value no member names would reach the searcher and fail there with
    /// no sentence a user could act on.
    /// </summary>
    [Fact]
    public void ASearchNamingAChecksumNoMemberNamesIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DuplicateSearch(MatchCriteria.Content, OneLocation, (ChecksumAlgorithm)99));
    }

    [Theory]
    [InlineData(20L, 10L)]
    [InlineData(-1L, null)]
    [InlineData(null, -1L)]
    public void ASizeRangeThatAdmitsNoFileIsRefused(long? smallest, long? largest)
    {
        Assert.Throws<ArgumentException>(() =>
            new DuplicateSearch(MatchCriteria.Size, OneLocation, sizes: new SizeRange(smallest, largest)));
    }

    /// <summary>
    /// An empty file is never matched, so a range whose largest size is nothing admits no file that
    /// could be, and a range of one byte is the smallest that admits one.
    /// </summary>
    [Theory]
    [InlineData(null, 0L, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(null, 1L, true)]
    [InlineData(0L, 1L, true)]
    public void ASizeRangeAdmittingOnlyEmptyFilesIsRefused(long? smallest, long? largest, bool accepted)
    {
        var sizes = new SizeRange(smallest, largest);

        Assert.Equal(accepted, DuplicateSearch.WhyRefused(MatchCriteria.Size, OneLocation, sizes) is null);

        if (accepted)
        {
            Assert.Equal(sizes, new DuplicateSearch(MatchCriteria.Size, OneLocation, sizes: sizes).Sizes);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new DuplicateSearch(MatchCriteria.Size, OneLocation, sizes: sizes));
        }
    }

    /// <summary>
    /// Each entry sits beside an extension, so the refusal is for the entry itself, not for a list
    /// left with nothing in it.
    /// </summary>
    [Theory]
    [InlineData(ExtensionFilterMode.OnlyThese, "*.*")]
    [InlineData(ExtensionFilterMode.OnlyThese, "*")]
    [InlineData(ExtensionFilterMode.OnlyThese, ".")]
    [InlineData(ExtensionFilterMode.OnlyThese, "jp?")]
    [InlineData(ExtensionFilterMode.OnlyThese, "tar.gz")]
    [InlineData(ExtensionFilterMode.SkipThese, "*")]
    public void AnExtensionFilterListingWhatIsNotAnExtensionIsRefused(ExtensionFilterMode mode, string entry)
    {
        var filter = new ExtensionFilter(mode, ["jpg", entry]);

        Assert.Contains($"'{entry}'", DuplicateSearch.WhyRefused(MatchCriteria.Size, OneLocation, extensions: filter), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new DuplicateSearch(MatchCriteria.Size, OneLocation, extensions: filter));
    }

    [Theory]
    [InlineData]
    [InlineData(" ")]
    public void AListOfExtensionsToSearchThatNamesNoneIsRefused(params string[] entries)
    {
        var filter = new ExtensionFilter(ExtensionFilterMode.OnlyThese, entries);

        Assert.NotNull(DuplicateSearch.WhyRefused(MatchCriteria.Size, OneLocation, extensions: filter));
        Assert.Throws<ArgumentException>(() => new DuplicateSearch(MatchCriteria.Size, OneLocation, extensions: filter));
    }

    /// <summary>
    /// What a person types into the box: a space between two extensions separates them, as a comma
    /// or a semicolon does. Read as one entry, <c>jpg png</c> would be an extension no file has, and
    /// the search would find nothing without saying why.
    /// </summary>
    [Theory]
    [InlineData("jpg png")]
    [InlineData("jpg, png")]
    [InlineData("*.jpg;*.png")]
    [InlineData(" .jpg ,\t.PNG ")]
    public void ATypedListIsSplitAtCommasSemicolonsAndSpaces(string typed)
    {
        var filter = ExtensionFilter.Parse(ExtensionFilterMode.OnlyThese, typed);

        Assert.Null(filter.WhyRefused);
        Assert.Equal([".jpg", ".png"], filter.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyTypedListToSearchIsRefused() =>
        Assert.NotNull(ExtensionFilter.Parse(ExtensionFilterMode.OnlyThese, " , ").WhyRefused);

    [Theory]
    [InlineData("jpg")]
    [InlineData(".JPG")]
    [InlineData("*.jpg")]
    [InlineData(" jpg ")]
    public void AnExtensionIsAcceptedHoweverItIsWritten(string entry)
    {
        var filter = new ExtensionFilter(ExtensionFilterMode.OnlyThese, [entry, " "]);

        Assert.Null(DuplicateSearch.WhyRefused(MatchCriteria.Size, OneLocation, extensions: filter));
        Assert.Equal([".jpg"], filter.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.True(filter.Admits("photo.jpg"));
        Assert.False(filter.Admits("photo.png"));
    }

    /// <summary>
    /// A filter refused for an entry is not the filter without it, though both list the same
    /// extensions, so a refused filter never stands in for one that can be searched with.
    /// </summary>
    [Fact]
    public void AFilterRefusedForAnEntryIsNotEqualToOneWithoutIt()
    {
        Assert.NotEqual(
            new ExtensionFilter(ExtensionFilterMode.OnlyThese, ["jpg", "*"]),
            new ExtensionFilter(ExtensionFilterMode.OnlyThese, ["jpg"]));
    }

    /// <summary>
    /// A list of extensions to skip that names none skips nothing, and a filter that searches every
    /// extension does not read its list, so neither is refused.
    /// </summary>
    [Fact]
    public void AFilterThatReadsNoExtensionIsAccepted()
    {
        Assert.Null(new ExtensionFilter(ExtensionFilterMode.SkipThese, []).WhyRefused);
        Assert.Null(new ExtensionFilter(ExtensionFilterMode.Any, ["*"]).WhyRefused);
    }

    [Theory]
    [InlineData(MatchCriteria.Content)]
    [InlineData(MatchCriteria.Name)]
    [InlineData(MatchCriteria.Name | MatchCriteria.Size | MatchCriteria.Modified)]
    public void ASearchWithACriterionAndALocationIsAccepted(MatchCriteria criteria)
    {
        Assert.Null(DuplicateSearch.WhyRefused(criteria, OneLocation));
        Assert.Equal(criteria, new DuplicateSearch(criteria, OneLocation).Criteria);
    }

    /// <summary>
    /// What a search found answers for the locations chosen only while they are the ones it searched,
    /// in the same roles: a folder made a reference afterwards, a location added or taken away, or a
    /// path that differs only in case each leaves its groups in roles the user no longer chose. The
    /// same locations listed in another order are the same search.
    /// </summary>
    [Fact]
    public void ASearchsResultsApplyOnlyToTheLocationsItSearchedInTheirRoles()
    {
        SearchLocation pictures = new(@"C:\Users\testuser\Pictures");
        SearchLocation backup = new(@"D:\Backup", LocationRole.Reference);
        var search = new DuplicateSearch(MatchCriteria.Content, [pictures, backup]);

        Assert.Null(search.WhyResultsDoNotApply([backup, pictures]));

        Assert.NotNull(search.WhyResultsDoNotApply([pictures with { Role = LocationRole.Reference }, backup]));
        Assert.NotNull(search.WhyResultsDoNotApply([pictures, backup with { Role = LocationRole.Search }]));
        Assert.NotNull(search.WhyResultsDoNotApply([pictures, backup, new(@"C:\Users\testuser\Pictures\Trips", LocationRole.Reference)]));
        Assert.NotNull(search.WhyResultsDoNotApply([pictures]));
        Assert.NotNull(search.WhyResultsDoNotApply([new(@"C:\Users\testuser\pictures"), backup]));
    }
}
