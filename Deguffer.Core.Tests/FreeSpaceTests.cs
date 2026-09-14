using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7 shows free space against capacity, so the two figures have to describe the same volume and
/// degrade together. Both are read from the volume holding a caller-supplied path — Core never
/// asks the environment where the profile is.
/// </summary>
public class FreeSpaceTests
{
    [Fact]
    public void ReportsCapacityForTheVolumeHoldingThePath()
    {
        using var temp = new TempDirectory();

        var total = FreeSpace.TotalForPath(temp.Path);
        var free = FreeSpace.ForPath(temp.Path);

        Assert.NotNull(total);
        Assert.NotNull(free);

        // The pairing is what the capacity bar draws: free above capacity would render a negative
        // used-fraction, and a zero capacity would divide by zero.
        Assert.True(total > 0);
        Assert.True(free <= total);

        // Strictly greater, so that returning free space from both accessors fails here. Any
        // volume able to hold this test's temp directory has something on it, so the two figures
        // are never equal in practice — and if they were, the bar would read empty on a full disk.
        Assert.True(total > free);
    }

    /// <summary>
    /// An unavailable volume is a dash in the UI, not an exception. The drive letter below is
    /// deliberately one Windows reserves for floppies and effectively never mounts.
    /// </summary>
    [Fact]
    public void ReturnsNullForAVolumeThatIsNotThere()
    {
        Assert.Null(FreeSpace.TotalForPath(@"B:\nonexistent\cache"));
        Assert.Null(FreeSpace.ForPath(@"B:\nonexistent\cache"));
    }

    [Fact]
    public void RejectsAPathThatCannotBeRooted()
    {
        Assert.Null(FreeSpace.TotalForPath(string.Empty));
        Assert.Null(FreeSpace.ForPath(string.Empty));
    }

    /// <summary>
    /// The figure the native route reads is the figure Windows reports for the same volume.
    ///
    /// <para><c>DriveInfo</c> is the independent witness here rather than the implementation: it is
    /// what this class used before the mount point was resolved natively, and it is not on the route
    /// under test. A rewrite that read the wrong field of <c>GetDiskFreeSpaceEx</c> — the raw free
    /// space rather than the caller's quota, or the total in place of the free — or that resolved a
    /// different volume, disagrees here.</para>
    ///
    /// <para><b>What this cannot show is the defect the rewrite fixed.</b> Telling the two routes
    /// apart needs a volume mounted at a folder, and a test cannot mount one. On a machine whose
    /// volumes all wear drive letters the two agree by construction, which is exactly what is
    /// asserted.</para>
    /// </summary>
    [Fact]
    public void ReportsTheSameFiguresTheVolumeItselfReports()
    {
        using var temp = new TempDirectory();

        var drive = new DriveInfo(Path.GetPathRoot(temp.Path)!);

        Assert.Equal(drive.TotalSize, FreeSpace.TotalForPath(temp.Path));

        // Free space moves between two reads on a working machine, so this is a bound rather than
        // an equality: the quota figure can never exceed the volume's capacity, and reading the
        // total into the free slot would break it on any disk holding anything.
        Assert.InRange(FreeSpace.ForPath(temp.Path)!.Value, 0, drive.TotalSize - 1);
    }

    /// <summary>
    /// §6.3: a path in extended-length form measures the same volume as the same path without the
    /// prefix. Every path in Core may arrive as <c>\\?\C:\…</c>, and the mount-point lookup is the
    /// one place in this class that hands a path to Win32.
    /// </summary>
    [Fact]
    public void ReadsThroughTheExtendedLengthPrefix()
    {
        using var temp = new TempDirectory();

        Assert.Equal(
            FreeSpace.TotalForPath(temp.Path),
            FreeSpace.TotalForPath(LongPath.Extended(temp.Path)));
    }

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
