using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>What the Clean button says about the selection it would clean.</summary>
public sealed class CleanOfferTests
{
    [Fact]
    public void StatesWhatTheTickedStepsGiveBack()
    {
        Assert.Equal("Clean 12.4 GB", CleanOffer.Label(ScanSize.FromLengths(13_314_398_618)));
    }

    /// <summary>The hedge a figure carries is part of what the button promises.</summary>
    [Fact]
    public void KeepsTheFiguresHedge()
    {
        Assert.Equal("Clean ~2.0 MB", CleanOffer.Label(ScanSize.Approximate(2 * 1024 * 1024)));
        Assert.Equal("Clean up to 3.0 MB", CleanOffer.Label(ScanSize.Ceiling(3 * 1024 * 1024)));
    }

    /// <summary>A leftover of empty folders frees no bytes and is still something the clean removes.</summary>
    [Fact]
    public void CountsEntriesWhereThereAreNoBytes()
    {
        Assert.Equal("Clean 4 items", CleanOffer.Label(ScanSize.Zero with { Entries = 4 }));
    }

    [Fact]
    public void SaysOnlyCleanWhenNothingWouldBeFreed()
    {
        Assert.Equal("Clean", CleanOffer.Label(ScanSize.Zero));
    }
}
