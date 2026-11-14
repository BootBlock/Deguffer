using System.Globalization;
using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Memory page says when its figures were read while live update is off, and only then (§7.2): on
/// the cadence they are never more than a couple of seconds old.
/// </summary>
public sealed class MemoryReadTimeTests
{
    private static readonly DateTimeOffset Read = new(2026, 6, 1, 14, 5, 9, TimeSpan.Zero);

    /// <summary>Five and a half hours ahead of UTC, so neither the hour nor the minute survives a lost conversion.</summary>
    private static readonly TimeZoneInfo Reader =
        TimeZoneInfo.CreateCustomTimeZone("Reader", TimeSpan.FromHours(5.5), "Reader", "Reader");

    [Fact]
    public void NothingIsSaidWhileLive() =>
        Assert.Empty(MemoryReadTime.Said(Read, live: true, Reader, CultureInfo.InvariantCulture));

    [Fact]
    public void NothingIsSaidBeforeTheFirstReading() =>
        Assert.Empty(MemoryReadTime.Said(null, live: false, Reader, CultureInfo.InvariantCulture));

    /// <summary>
    /// In the reader's own time zone and time format: a 24-hour culture and a 12-hour one read the same
    /// moment differently, which a fixed pattern would not.
    /// </summary>
    [Fact]
    public void OffItSaysWhenInTheReadersZoneAndFormat()
    {
        Assert.Equal("Read at 19:35:09", MemoryReadTime.Said(Read, live: false, Reader, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Read at 7:35:09 PM", MemoryReadTime.Said(Read, live: false, Reader, CultureInfo.GetCultureInfo("en-US")));
    }
}
