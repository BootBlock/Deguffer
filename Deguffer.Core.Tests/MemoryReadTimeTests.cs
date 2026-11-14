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

    [Fact]
    public void NothingIsSaidWhileLive() =>
        Assert.Empty(MemoryReadTime.Said(Read, live: true, CultureInfo.InvariantCulture));

    [Fact]
    public void NothingIsSaidBeforeTheFirstRead() =>
        Assert.Empty(MemoryReadTime.Said(null, live: false, CultureInfo.InvariantCulture));

    /// <summary>
    /// In the reader's own clock and the reader's own time format: a 24-hour culture and a 12-hour one
    /// read the same moment differently, which a fixed pattern would not.
    /// </summary>
    [Fact]
    public void OffItSaysWhenInTheReadersClockAndFormat()
    {
        var local = Read.ToLocalTime();

        Assert.Equal(
            $"Read at {local.Hour:00}:{local.Minute:00}:{local.Second:00}",
            MemoryReadTime.Said(Read, live: false, CultureInfo.GetCultureInfo("en-GB")));

        Assert.EndsWith(
            local.Hour < 12 ? "AM" : "PM",
            MemoryReadTime.Said(Read, live: false, CultureInfo.GetCultureInfo("en-US")));
    }
}
