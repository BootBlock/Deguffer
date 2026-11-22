using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>#751: a drive database's POSIX patterns, matched against a whole model or firmware as smartctl matches them.</summary>
public sealed class PosixPatternTests
{
    private static bool Matches(string pattern, string text) => PosixPattern.Compile(pattern)!.IsMatch(text);

    [Theory]
    [InlineData("TEST SSD [0-9]{3}GB", "TEST SSD 512GB", true)]
    [InlineData("TEST SSD [0-9]{3}GB", "TEST SSD 512GB2", false)]
    [InlineData("TEST SSD [0-9]{3}GB", "XTEST SSD 512GB", false)]
    [InlineData("TEST (A|B)+", "TEST ABBA", true)]
    [InlineData("TEST.?X", "TESTX", true)]
    [InlineData("TEST [0-9]{2,}", "TEST 1", false)]
    [InlineData("TEST [0-9]{2,3}", "TEST 1234", false)]
    [InlineData("^TEST$", "TEST", true)]
    [InlineData("TEST\\.1", "TEST.1", true)]
    [InlineData("TEST\\.1", "TESTX1", false)]
    [InlineData("TEST\\(1\\)", "TEST(1)", true)]
    [InlineData("TEST-[^0-9]", "TEST-A", true)]
    [InlineData("TEST-[^0-9]", "TEST-5", false)]
    public void MatchesTheWholeString(string pattern, string text, bool expected) => Assert.Equal(expected, Matches(pattern, text));

    [Fact]
    public void ABackslashInABracketIsItself() => Assert.True(Matches(@"A[\]B", @"A\B"));

    [Fact]
    public void AClosingBracketFirstInABracketIsAMember()
    {
        Assert.True(Matches("A[]x]B", "A]B"));
        Assert.True(Matches("A[^]x]B", "AzB"));
        Assert.False(Matches("A[^]x]B", "A]B"));
    }

    [Fact]
    public void AHyphenFirstOrLastInABracketIsItself()
    {
        Assert.True(Matches("A[-x]B", "A-B"));
        Assert.True(Matches("A[x-]B", "A-B"));
        Assert.False(Matches("A[x-]B", "AyB"));
    }

    [Fact]
    public void ADotMatchesAnyCharacter() => Assert.True(Matches("A.B", "A\nB"));

    [Fact]
    public void CaseCounts() => Assert.False(Matches("TEST", "test"));

    [Theory]
    [InlineData("TEST[[:digit:]]")]
    [InlineData("TEST[[=a=]]")]
    [InlineData(@"TEST\d")]
    [InlineData("(?i)TEST")]
    [InlineData("TEST{a}")]
    [InlineData("TEST{2")]
    [InlineData("TEST[0-9")]
    [InlineData("*TEST")]
    [InlineData("TEST\\")]
    public void RefusesSyntaxItDoesNotTranslate(string pattern) => Assert.Null(PosixPattern.Compile(pattern));
}
