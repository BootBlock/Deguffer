using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7 shows free space before and after, so every size on screen is stated in these words. The
/// figures themselves are read through <see cref="IVolumeInventory.SpaceOf"/>; see
/// <c>VolumeInventoryTests</c>.
/// </summary>
public class FreeSpaceTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536 * 1024, "1.5 MB")]
    [InlineData(1L << 50, "1.0 PB")]
    [InlineData(long.MaxValue, "8.0 EB")]
    public void FormatsSizesInTheBinaryUnitsWindowsReports(long bytes, string expected) =>
        Assert.Equal(expected, FreeSpace.Format(bytes));

    /// <summary>
    /// A drive's lifetime counter can pass the largest <see cref="long"/>. Holding it there would state
    /// a total about a thousand times too small, so the wide form goes on to the larger units.
    /// </summary>
    [Fact]
    public void StatesASizePastTheLargestLongInFull()
    {
        Assert.Equal("1.0 ZB", FreeSpace.FormatWide(UInt128.One << 70));
        Assert.Equal("1.0 YB", FreeSpace.FormatWide(UInt128.One << 80));
        Assert.Equal("1.5 MB", FreeSpace.FormatWide(1536U * 1024));
    }

    /// <summary>
    /// §5.4 reports the free-space *change*, which is negative whenever the machine wrote more
    /// during the run than Deguffer removed. Dropping the sign would report a loss as a gain.
    /// </summary>
    [Fact]
    public void KeepsTheSignOnANegativeChange() =>
        Assert.Equal("-1.5 MB", FreeSpace.Format(-1536 * 1024));

    /// <summary>
    /// A figure a tool produced about its own future behaviour is hedged. Conda's dry run is the
    /// caller: it says what its clean expects to free, and its next run may disagree.
    /// </summary>
    [Fact]
    public void SaysWhenASizeIsAPredictionRatherThanAMeasurement() =>
        Assert.Equal("about 1.5 MB", FreeSpace.Format(ScanSize.Approximate(1536 * 1024)));

    /// <summary>
    /// A ceiling the removal is known to fall short of says "up to", never "about", and stays a ceiling
    /// in any total it is part of.
    /// </summary>
    [Fact]
    public void ACeilingSaysUpToAndStaysOneInATotal()
    {
        Assert.Equal("up to 1.5 MB", FreeSpace.Format(ScanSize.Ceiling(1536 * 1024)));
        Assert.Equal("up to 2.0 MB", FreeSpace.Format(ScanSize.Ceiling(1536 * 1024) + ScanSize.FromLengths(512 * 1024)));
        Assert.Equal("up to 2.0 MB", FreeSpace.Format(ScanSize.Approximate(512 * 1024) + ScanSize.Ceiling(1536 * 1024)));
    }

    /// <summary>
    /// §5.5's fallback walk is <em>not</em> hedged, and this is the assertion that changed when
    /// <see cref="ScanSize.Reclaimable"/> became the logical figure. The walk reports file lengths;
    /// file lengths are now the number reported; so the walk measures the reported number exactly,
    /// and "about" would be a qualification on every unelevated preview with nothing behind it.
    /// </summary>
    [Fact]
    public void DoesNotHedgeAWalkedSizeBecauseTheWalkMeasuresTheReportedNumberExactly() =>
        Assert.Equal("1.5 MB", FreeSpace.Format(ScanSize.FromLengths(1536 * 1024)));

    /// <summary>
    /// A leftover of empty folders frees no bytes, and "0 B" beside a row ready to clean would read as
    /// nothing to do. What it offers is its entries, so that is what is stated.
    /// </summary>
    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(428, "428 items")]
    public void StatesTheEntriesOfSomethingThatFreesNoBytes(long entries, string expected) =>
        Assert.Equal(expected, FreeSpace.Format(new ScanSize(0, 0, Entries: entries)));

    /// <summary>
    /// Bytes are the figure wherever there are any. The count stands in for them where there are none,
    /// rather than becoming a second number in every label.
    /// </summary>
    [Fact]
    public void StatesBytesWhereThereAreAnyWhateverTheCount() =>
        Assert.Equal("1.5 MB", FreeSpace.Format(new ScanSize(1536 * 1024, 1536 * 1024, Entries: 300)));

    [Fact]
    public void StatesNothingAsNoBytes() =>
        Assert.Equal("0 B", FreeSpace.Format(ScanSize.Zero));

    /// <summary>
    /// The number stated is the logical one. The allocated figure is deliberately different here,
    /// so this fails rather than passing by coincidence if the two axes are ever swapped back —
    /// <see cref="ScanSize.Reclaimable"/> carries the three measurements that decided it.
    /// </summary>
    [Fact]
    public void StatesAMeasuredSizeAsTheLogicalFigure() =>
        Assert.Equal("8.6 MB", FreeSpace.Format(new ScanSize(1536 * 1024, 9_000_000)));
}
