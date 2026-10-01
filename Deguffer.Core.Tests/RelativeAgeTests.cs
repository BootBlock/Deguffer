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

    /// <summary>
    /// A local timestamp must be converted, not read as though it were already UTC. Read unconverted
    /// it is out by the zone's offset: hours older than it is behind UTC, and hours newer ahead of it.
    ///
    /// <para>The day count truncates, so the gap is chosen from the sign of the offset to put that
    /// error across a day boundary whichever side of UTC the machine is on. Ahead, three days and
    /// half the offset reads unconverted as two. Behind, four days less half the offset reads as
    /// four. The instant comes from <see cref="LocalZone"/>, so a machine set to UTC says that it
    /// could not discriminate rather than passing as though it had.</para>
    /// </summary>
    [Fact]
    public void ComparesInUtcRegardlessOfTheKindItIsGiven()
    {
        var offsetAt = LocalZone.OffsetInstant(Now.Year);
        var written = offsetAt ?? Now;
        var offset = TimeZoneInfo.Local.GetUtcOffset(written);
        var gap = TimeSpan.FromDays(offset < TimeSpan.Zero ? 4 : 3) + offset / 2;
        var local = written.ToLocalTime();

        Assert.Equal("3 days ago", RelativeAge.Describe(local, written + gap));

        // What was actually proved: on UTC an unconverted reading is the same reading.
        Assert.Equal(offsetAt is not null, local.Ticks != written.Ticks);
    }
}
