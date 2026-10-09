using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Tests;

/// <summary>What a group says about itself (§7.4): a group not matched on its content says its files may differ.</summary>
public sealed class DuplicateGroupTests
{
    [Theory]
    [InlineData(MatchCriteria.Name)]
    [InlineData(MatchCriteria.Size)]
    [InlineData(MatchCriteria.Modified)]
    [InlineData(MatchCriteria.Name | MatchCriteria.Size | MatchCriteria.Modified)]
    public void AGroupNotMatchedOnContentSaysItsFilesMayDiffer(MatchCriteria criteria) =>
        Assert.Contains("may differ", new DuplicateGroup(criteria, null, null, []).MayDiffer, StringComparison.Ordinal);

    [Theory]
    [InlineData(MatchCriteria.Content)]
    [InlineData(MatchCriteria.Content | MatchCriteria.Name)]
    public void AContentGroupSaysNothingOfTheSort(MatchCriteria criteria) =>
        Assert.Null(new DuplicateGroup(criteria, 100, null, []).MayDiffer);

    [Theory]
    [InlineData(MatchCriteria.Name, "the name")]
    [InlineData(MatchCriteria.Name | MatchCriteria.Size, "the name and size")]
    [InlineData(MatchCriteria.Name | MatchCriteria.Size | MatchCriteria.Modified | MatchCriteria.Content, "the name, size, last-modified time and content")]
    public void TheCriteriaAreDescribedInWords(MatchCriteria criteria, string words) =>
        Assert.Equal(words, criteria.Described());

    /// <summary>Each algorithm is named as other tools print it, so a value can be compared with theirs.</summary>
    [Fact]
    public void EachChecksumIsNamedAsOtherToolsNameIt() =>
        Assert.Equal(
            ["XXH128", "SHA-256", "SHA-512", "SHA-1", "MD5", "CRC-32", "SHA3-256"],
            Enum.GetValues<ChecksumAlgorithm>().Select(algorithm => algorithm.Name()));
}
