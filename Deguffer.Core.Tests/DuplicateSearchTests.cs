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

    [Theory]
    [InlineData(20L, 10L)]
    [InlineData(-1L, null)]
    [InlineData(null, -1L)]
    public void ASizeRangeThatAdmitsNoFileIsRefused(long? smallest, long? largest)
    {
        Assert.Throws<ArgumentException>(() =>
            new DuplicateSearch(MatchCriteria.Size, OneLocation, sizes: new SizeRange(smallest, largest)));
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
}
