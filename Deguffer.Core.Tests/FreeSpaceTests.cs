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
}
