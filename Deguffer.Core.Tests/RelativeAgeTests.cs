using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7: age is a first-class column, and "last touched 5 months ago" is the sentence it has to
/// produce. The case that matters most is the absent one — a missing timestamp must read as
/// unknown, never as old, because "old" is what invites the user to delete it.
/// </summary>
public sealed class RelativeAgeTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnAbsentTimestampIsUnknownAndNeverOld()
    {
        var label = RelativeAge.Describe(null, Now);

        Assert.Equal("Unknown", label);
        Assert.DoesNotContain("ago", label, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(3, "3 days ago")]
    [InlineData(6, "6 days ago")]
    [InlineData(7, "1 week ago")]
    [InlineData(20, "2 weeks ago")]
    [InlineData(31, "1 month ago")]
    [InlineData(150, "5 months ago")]
    [InlineData(365, "1 year ago")]
    [InlineData(900, "2 years ago")]
    public void DescribesHowLongAgoInTheUnitAReaderWouldUse(int daysAgo, string expected) =>
        Assert.Equal(expected, RelativeAge.Describe(Now.AddDays(-daysAgo), Now));

    /// <summary>
    /// Clock skew and a file written during the scan both produce a future timestamp. Reporting
    /// "in 3 days" would be nonsense; reporting it as ancient would be dangerous.
    /// </summary>
    [Fact]
    public void AFutureTimestampReadsAsTodayRatherThanAsAnAge()
    {
        Assert.Equal("Today", RelativeAge.Describe(Now.AddDays(3), Now));
    }

    /// <summary>
    /// A single day is "Yesterday" by design rather than "1 day ago", so the singular forms worth
    /// pinning are the ones the pluraliser actually produces.
    /// </summary>
    [Fact]
    public void SingularAndPluralAreBothWellFormed()
    {
        Assert.Equal("2 days ago", RelativeAge.Describe(Now.AddDays(-2), Now));
        Assert.Equal("1 week ago", RelativeAge.Describe(Now.AddDays(-7), Now));
        Assert.Equal("2 weeks ago", RelativeAge.Describe(Now.AddDays(-14), Now));
        Assert.Equal("1 month ago", RelativeAge.Describe(Now.AddDays(-31), Now));
        Assert.Equal("2 months ago", RelativeAge.Describe(Now.AddDays(-61), Now));
        Assert.Equal("1 year ago", RelativeAge.Describe(Now.AddDays(-365), Now));
        Assert.Equal("2 years ago", RelativeAge.Describe(Now.AddDays(-730), Now));
    }

    /// <summary>A local timestamp must not read as several hours older than it is.</summary>
    [Fact]
    public void ComparesInUtcRegardlessOfTheKindItIsGiven()
    {
        var local = Now.AddDays(-3).ToLocalTime();

        Assert.Equal("3 days ago", RelativeAge.Describe(local, Now));
    }
}
