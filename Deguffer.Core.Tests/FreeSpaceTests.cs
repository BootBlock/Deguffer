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
    public void FormatsSizesInTheBinaryUnitsWindowsReports(long bytes, string expected) =>
        Assert.Equal(expected, FreeSpace.Format(bytes));

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
