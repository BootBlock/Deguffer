using System.Globalization;
using Deguffer.Core.Text;

namespace Deguffer.Core.Tests;

/// <summary>Every count reads the same way: grouped in the reader's culture, and in the singular only for one.</summary>
public sealed class CountedTests
{
    [Theory]
    [InlineData(0, "0 copies")]
    [InlineData(1, "1 copy")]
    [InlineData(2, "2 copies")]
    [InlineData(1_000, "1,000 copies")]
    [InlineData(1_234, "1,234 copies")]
    public void TheNounFollowsTheCount(int count, string expected) =>
        Assert.Equal(expected, Counted.Of(count, "copy", "copies"));

    /// <summary>Drive figures arrive as 128-bit counts, and a count past a long still groups and agrees.</summary>
    [Fact]
    public void AWideCountReadsTheSameWay()
    {
        Assert.Equal("1 hour", Counted.Of(UInt128.One, "hour", "hours"));
        Assert.Equal("18,446,744,073,709,551,616 hours", Counted.Of((UInt128)ulong.MaxValue + 1, "hour", "hours"));
    }

    [Theory]
    [InlineData(0, "are")]
    [InlineData(1, "is")]
    [InlineData(2, "are")]
    [InlineData(1_000, "are")]
    public void TheFormAgreesWithTheCount(long count, string expected) =>
        Assert.Equal(expected, Counted.Form(count, "is", "are"));

    [Fact]
    public void TheGroupSeparatorIsTheReadersOwn()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.234 copies", Counted.Of(1_234, "copy", "copies"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
