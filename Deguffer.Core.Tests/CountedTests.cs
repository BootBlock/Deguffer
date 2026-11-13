using System.Globalization;
using Deguffer.Core.Text;

namespace Deguffer.Core.Tests;

/// <summary>Every count reads the same way: grouped in the reader's culture, and in the singular only for one.</summary>
public sealed class CountedTests
{
    [Theory]
    [InlineData(0UL, "0 copies")]
    [InlineData(1UL, "1 copy")]
    [InlineData(2UL, "2 copies")]
    [InlineData(1_234UL, "1,234 copies")]
    public void TheNounFollowsTheCount(ulong count, string expected) =>
        Assert.Equal(expected, Counted.Of(count, "copy", "copies"));

    [Fact]
    public void TheGroupSeparatorIsTheReadersOwn()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.234 copies", Counted.Of(1_234UL, "copy", "copies"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
