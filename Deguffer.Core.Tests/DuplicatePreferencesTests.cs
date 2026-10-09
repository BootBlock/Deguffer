using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Duplicates page searches with, as stored (§7.4): every value survives the file, a file
/// from before the page reads with §7.4's defaults, and the search built from the values is the one
/// they describe.
/// </summary>
public sealed class DuplicatePreferencesTests
{
    private static readonly IReadOnlyList<SearchLocation> Photos = [new(@"C:\Users\testuser\Pictures")];

    /// <summary>Every member set away from its default, so a member the file drops is one the test sees.</summary>
    private static readonly DuplicatePreferences EveryValueChanged = new()
    {
        Criteria = MatchCriteria.Name | MatchCriteria.Modified,
        Algorithm = ChecksumAlgorithm.Sha256,
        Smallest = new SizeLimit(1.5, SizeUnit.MB),
        Largest = new SizeLimit(null, SizeUnit.GB),
        ExtensionMode = ExtensionFilterMode.SkipThese,
        Extensions = "tmp, log",
        SearchHidden = true,
        SearchSystem = true,
        SearchPassedOverPlaces = true,
    };

    [Fact]
    public void EveryValueSurvivesTheFile()
    {
        using var temp = new TempDirectory();
        var store = new PreferenceStore(new FakeUserEnvironment(temp.Path));

        Assert.True(store.Save(AppPreferences.Default with { Duplicates = EveryValueChanged }));

        Assert.Equal(EveryValueChanged, store.Load().Duplicates);
    }

    /// <summary>
    /// A file written before the page existed is every file on an upgraded machine. It reads with a
    /// content match by XXH128, every size and extension, and neither hidden nor system files.
    /// </summary>
    [Fact]
    public void AFileFromBeforeThePageReadsWithTheDefaults()
    {
        var loaded = LoadWritten("""{ "Theme": "Dark" }""");

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(DuplicatePreferences.Default, loaded.Duplicates);
        Assert.Equal(MatchCriteria.Content, loaded.Duplicates.Criteria);
        Assert.Equal(ChecksumAlgorithm.XxHash128, loaded.Duplicates.Algorithm);
        Assert.False(loaded.Duplicates.SearchHidden);
        Assert.False(loaded.Duplicates.SearchSystem);
        Assert.False(loaded.Duplicates.SearchPassedOverPlaces);
    }

    [Fact]
    public void AValueTheFileDoesNotMentionTakesItsDefault()
    {
        var loaded = LoadWritten("""{ "Duplicates": { "Criteria": "Name, Size" } }""");

        Assert.Equal(MatchCriteria.Name | MatchCriteria.Size, loaded.Duplicates.Criteria);
        Assert.Equal(SizeLimit.None, loaded.Duplicates.Smallest);
        Assert.Equal(string.Empty, loaded.Duplicates.Extensions);
    }

    /// <summary>The search the values describe, member by member.</summary>
    [Fact]
    public void TheSearchIsTheOneTheValuesDescribe()
    {
        var search = EveryValueChanged.Search(Photos);

        Assert.Equal(MatchCriteria.Name | MatchCriteria.Modified, search.Criteria);
        Assert.Equal(Photos, search.Locations);
        Assert.Equal(ChecksumAlgorithm.Sha256, search.Algorithm);
        Assert.Equal(new SizeRange(1_572_864, null), search.Sizes);
        Assert.Equal(ExtensionFilterMode.SkipThese, search.Extensions.Mode);
        Assert.Equal([".log", ".tmp"], search.Extensions.Extensions);
        Assert.True(search.SearchHidden);
        Assert.True(search.SearchSystem);
        Assert.True(search.SearchPassedOverPlaces);
    }

    /// <summary>
    /// A checksum this machine does not offer, from a file written on one that does or edited by
    /// hand, searches with the default rather than ending the search with an error.
    /// </summary>
    [Fact]
    public void AChecksumThisMachineDoesNotOfferSearchesWithTheDefault()
    {
        var stored = DuplicatePreferences.Default with { Algorithm = (ChecksumAlgorithm)99 };

        Assert.Equal(ChecksumAlgorithm.XxHash128, stored.OfferedAlgorithm);
        Assert.Equal(ChecksumAlgorithm.XxHash128, stored.Search(Photos).Algorithm);
    }

    [Theory]
    [InlineData(1, SizeUnit.Bytes, 1L)]
    [InlineData(1, SizeUnit.KB, 1024L)]
    [InlineData(1.5, SizeUnit.MB, 1_572_864L)]
    [InlineData(2, SizeUnit.GB, 2_147_483_648L)]
    [InlineData(0.0004, SizeUnit.KB, 0L)]
    [InlineData(1e30, SizeUnit.GB, long.MaxValue)]
    [InlineData(-1, SizeUnit.KB, -1024L)]
    public void ALimitIsItsAmountInItsUnit(double amount, SizeUnit unit, long bytes) =>
        Assert.Equal(bytes, new SizeLimit(amount, unit).Bytes);

    /// <summary>An empty box, which a number box gives as not a number, is no limit, whatever unit is chosen.</summary>
    [Fact]
    public void NoAmountIsNoLimit()
    {
        Assert.Null(new SizeLimit(null, SizeUnit.GB).Bytes);
        Assert.Null(new SizeLimit(double.NaN, SizeUnit.GB).Bytes);
        Assert.Null(SizeLimit.Entered(double.NaN));
        Assert.Equal(5, SizeLimit.Entered(5));
    }

    /// <summary>A negative size is refused with its reason, rather than read as no limit.</summary>
    [Fact]
    public void ANegativeSizeIsRefusedRatherThanIgnored()
    {
        var stored = DuplicatePreferences.Default with { Smallest = new SizeLimit(-1, SizeUnit.MB) };

        Assert.Equal("A size cannot be less than nothing.", stored.WhyRefused(Photos));
    }

    [Fact]
    public void WhyRefusedAsksTheSearchItBuilds()
    {
        Assert.Null(DuplicatePreferences.Default.WhyRefused(Photos));
        Assert.Equal("Choose at least one drive or folder to search.", DuplicatePreferences.Default.WhyRefused([]));
        Assert.Equal(
            "Choose at least one thing two files must share to be a match.",
            (DuplicatePreferences.Default with { Criteria = MatchCriteria.None }).WhyRefused(Photos));
    }

    private static AppPreferences LoadWritten(string json)
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(Path.Combine(directory.FullName, "preferences.json"), json);

        return new PreferenceStore(environment).Load();
    }
}
