using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's fallback, tested on its own against a real tree.
///
/// Deliberately independent of the MFT path: this is what runs on an unelevated machine, which
/// §6.3 says is the default, so it is the route most users will actually get. A suite that only
/// exercised the fast path would leave the common case unverified.
/// </summary>
public class ParallelEnumerationScannerTests
{
    private static readonly ParallelEnumerationScanner Scanner = ParallelEnumerationScanner.Default;

    [Fact]
    public async Task TotalsANestedTree()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "nested", "b.bin");
        temp.CreateFile(3000, "cache", "nested", "deeper", "c.bin");

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(6000, result.Size.Logical);
    }

    /// <summary>§5.6's shape: what was excluded matters as much as what was counted.</summary>
    [Fact]
    public async Task ExcludesSiblingDirectories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "counted.bin");
        temp.CreateFile(9_000_000, "config", "untouched.bin");

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(1000, result.Size.Logical);
    }

    /// <summary>
    /// An absent cache is a normal answer, not an error — providers ask about locations that may
    /// never have been created.
    /// </summary>
    [Fact]
    public async Task MeasuresAMissingDirectoryAsZero()
    {
        using var temp = new TempDirectory();

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "never-created"));

        Assert.Equal(0, result.Size.Logical);
    }

    /// <summary>
    /// This route does not hedge its sizes, and that reverses what it used to do.
    ///
    /// <para>The flag meant "this could not see allocated bytes", which was true and, once
    /// <see cref="ScanSize.Reclaimable"/> became the logical figure, no longer relevant to the
    /// number anybody reads: the walk sums file lengths and file lengths are what gets reported, to
    /// the byte. §6.3 makes this the ordinary route, so leaving the flag on would have printed
    /// "about" in front of every size on an unelevated machine — a qualification with nothing
    /// behind it, which is the opposite of what the flag exists for.</para>
    ///
    /// <para>What still carries it is a figure that is a forecast: conda's dry run, and
    /// <see cref="HardLinkAwareScanner"/>'s sole-link sum.</para>
    /// </summary>
    [Fact]
    public async Task DoesNotHedgeSizesItMeasuredExactly()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1024, "cache", "a.bin");

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.False(result.Size.IsApproximate);
        Assert.Equal(1024, result.Size.Reclaimable);
        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
    }

    /// <summary>
    /// A file only a deep walk reaches is still counted, which is the failure worth guarding
    /// against: a truncation would stop counting partway down a <c>node_modules</c> tree rather than
    /// failing, and the total would simply be too small.
    ///
    /// Whether <c>LongPath</c> is what got the walk there is not observable from a total. .NET
    /// prefixes past 260 characters itself, so this stays green with the prefixing deleted; the
    /// property that does discriminate for this walk is asserted in
    /// <see cref="BoundedFileWalkTests.CarriesTheFormOfTheRootDownToEveryFileItVisits"/>.
    /// </summary>
    [Fact]
    public async Task CountsFilesBeyondMaxPath()
    {
        using var temp = new TempDirectory();

        var deep = Path.Combine(temp.Path, "cache");
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[4096]);

        Assert.True(deep.Length > 260);

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(4096, result.Size.Logical);
    }

    /// <summary>§5.5: the reason for taking the slow route travels with the number.</summary>
    [Fact]
    public async Task CarriesTheReasonItWasUsed()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(10, "cache", "a.bin");

        var result = await Scanner
            .Because(FallbackReason.NotElevated)
            .MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(FallbackReason.NotElevated, result.Fallback);
        Assert.Contains("administrator", result.FallbackNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsPartialTotalsWhileItWorks()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "nested", "b.bin");
        temp.CreateFile(3000, "cache", "nested", "deeper", "c.bin");

        var progress = new ProgressRecorder<ScanSize>();

        var result = await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"), MinimumAge.Off, progress);

        // §5.5: partial totals reach the caller as the walk descends, rather than one number at
        // the end. The tree is three levels deep, so there is more than one level to report.
        Assert.Equal(6000, result.Size.Logical);
        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, size => Assert.InRange(size.Logical, 0, 6000));
        Assert.Equal(6000, progress.Reports[^1].Logical);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 200; i++)
        {
            temp.CreateFile(64, "cache", $"dir{i}", "file.bin");
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Scanner.MeasureAsync(Path.Combine(temp.Path, "cache"), MinimumAge.Off, progress: null, cancelled.Token));
    }

    /// <summary>
    /// §5.5's walk, with the guard applied. The figure and the deletion have to describe the same
    /// set of files: a preview naming bytes the clean will not take is §5.4's broken promise
    /// arriving from the other direction.
    /// </summary>
    [Fact]
    public async Task ExcludesFilesInsideTheGuardWindowFromTheTotal()
    {
        using var temp = new TempDirectory();
        TempDirectory.Age(temp.CreateFile(4096, "cache", "stale.bin"), TimeSpan.FromDays(30));
        temp.CreateFile(1_000_000, "cache", "deep", "written-just-now.bin");

        var cache = Path.Combine(temp.Path, "cache");

        var unguarded = await Scanner.MeasureAsync(cache);
        var guarded = await Scanner.MeasureAsync(cache, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal(1_004_096, unguarded.Size.Reclaimable);
        Assert.Equal(4096, guarded.Size.Reclaimable);
    }

    /// <summary>
    /// A single named file is measured on its own path (<see cref="ScanStrategy.DirectRead"/>), so
    /// the guard has to be applied there too. Without it a protected <c>MEMORY.DMP</c> would preview
    /// as gigabytes the run is not going to take.
    /// </summary>
    [Fact]
    public async Task MeasuresAGuardedSingleFileAsNothing()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile(8192, "dumps", "MEMORY.DMP");

        var guarded = await Scanner.MeasureAsync(file, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal(ScanStrategy.DirectRead, guarded.Strategy);
        Assert.Equal(0, guarded.Size.Reclaimable);
        Assert.Equal(8192, (await Scanner.MeasureAsync(file)).Size.Reclaimable);
    }

    /// <summary>
    /// A zero is ambiguous once a guard is in force, and the shell makes a claim out of a zero. So
    /// the measurement says which zero it is: an empty location and one whose every file is inside
    /// the window are the two cases, and only the walk can tell them apart.
    /// </summary>
    [Fact]
    public async Task SaysWhetherItWithheldAnythingRatherThanLeavingAZeroToBeGuessedAt()
    {
        using var temp = new TempDirectory();
        var empty = temp.CreateDirectory("empty");
        var recent = temp.CreateDirectory("recent");
        temp.CreateFile(4096, "recent", "written-just-now.bin");

        var guard = MinimumAge.WithinHours(8, DateTime.UtcNow);

        var wholesaleRecent = await Scanner.MeasureAsync(recent, guard);
        var genuinelyEmpty = await Scanner.MeasureAsync(empty, guard);

        Assert.Equal(0, wholesaleRecent.Size.Reclaimable);
        Assert.Equal(0, genuinelyEmpty.Size.Reclaimable);

        Assert.True(wholesaleRecent.WithheldRecent, "a full location was reported as holding nothing back");
        Assert.False(genuinelyEmpty.WithheldRecent, "an empty location claimed the guard held something");

        // And nothing is claimed where no guard was asked for.
        Assert.False((await Scanner.MeasureAsync(recent)).WithheldRecent);
    }
}
