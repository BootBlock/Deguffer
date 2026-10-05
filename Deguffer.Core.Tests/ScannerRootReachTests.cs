using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A path the walk could not reach is reported as not reached, never as empty.
///
/// <para>Both walking scanners are held to it, because every figure Deguffer shows comes from one of
/// them and a zero is read as "Already clear" by the preview and as "everything went" by the
/// executor. The refusals are real access rules, because what is under test is how Windows refuses.
/// See <see cref="RootReach"/>.</para>
/// </summary>
public class ScannerRootReachTests
{
    public static TheoryData<string> Scanners => [nameof(ParallelEnumerationScanner), nameof(HardLinkAwareScanner)];

    private static IDirectoryScanner Scanner(string name) => name switch
    {
        nameof(ParallelEnumerationScanner) => ParallelEnumerationScanner.Default,
        _ => HardLinkAwareScanner.Default,
    };

    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task ARootWindowsWillNotDescribeIsNotReachedRatherThanEmpty(string scanner)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "tools", "cache", "a.bin");
        var cache = Path.Combine(temp.Path, "tools", "cache");

        using var denied = DeniedDirectory.WithUnreadableAttributes(cache);

        var result = await Scanner(scanner).MeasureAsync(cache);

        Assert.Equal(RootReach.NotDescribed, result.Root);
        Assert.False(result.WasReached);
        Assert.Equal(0, result.Size.Logical);
    }

    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task ARootThatWillNotBeListedIsNotReachedRatherThanEmpty(string scanner)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        var cache = Path.Combine(temp.Path, "cache");

        using var denied = new DeniedDirectory(cache);

        var result = await Scanner(scanner).MeasureAsync(cache);

        Assert.Equal(RootReach.NotListed, result.Root);
        Assert.Equal(0, result.Size.Logical);
    }

    /// <summary>
    /// A file is a subject like a directory, and a refused one used to read as "not a file" and then
    /// as "not a directory either", which is to say as nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task AFileWindowsWillNotDescribeIsNotReached(string scanner)
    {
        using var temp = new TempDirectory();
        var dump = temp.CreateFile(4096, "dumps", "MEMORY.DMP");

        using var denied = DeniedDirectory.WithUnreadableFile(dump);

        var result = await Scanner(scanner).MeasureAsync(dump);

        Assert.Equal(RootReach.NotDescribed, result.Root);
        Assert.Equal(0, result.Size.Logical);
    }

    /// <summary>
    /// The decision <see cref="RootReach"/> records: a folder refused below the root contributes
    /// nothing, because a removal running as the same account is refused the same listing and cannot
    /// take it either. The root was read, so the figure stands.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task AFolderRefusedInsideTheWalkLeavesTheRootReached(string scanner)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(1000, "cache", "a.bin");
        temp.CreateFile(2000, "cache", "locked", "b.bin");
        var cache = Path.Combine(temp.Path, "cache");

        using var denied = new DeniedDirectory(Path.Combine(cache, "locked"));

        var result = await Scanner(scanner).MeasureAsync(cache);

        Assert.Equal(RootReach.Reached, result.Root);
        Assert.Equal(1000, result.Size.Logical);
    }

    /// <summary>An absent path holds nothing, and saying so is a complete answer.</summary>
    [Theory]
    [MemberData(nameof(Scanners))]
    public async Task AnAbsentPathIsReachedAndEmpty(string scanner)
    {
        using var temp = new TempDirectory();

        var result = await Scanner(scanner).MeasureAsync(Path.Combine(temp.Path, "never-created"));

        Assert.True(result.WasReached);
        Assert.Equal(0, result.Size.Logical);
    }

    /// <summary>
    /// A zero nobody measured is not last run's figure. Remembered, it would open the next run on a
    /// refused cache as though it were empty, replacing the last figure anybody actually read.
    /// </summary>
    [Fact]
    public async Task AnUnreachedRootDoesNotReplaceTheFigureTheNextRunOpensOn()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.CreateDirectory("profile"));
        temp.CreateFile(1000, "tools", "cache", "a.bin");
        var cache = Path.Combine(temp.Path, "tools", "cache");

        await Reopen(environment).MeasureAsync(cache);

        using (DeniedDirectory.WithUnreadableAttributes(cache))
        {
            Assert.False((await Reopen(environment).MeasureAsync(cache)).WasReached);
            Assert.False((await Reopen(environment).MeasureFromDiskAsync(cache)).WasReached);
        }

        var progress = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress);

        Assert.Equal(1000, progress.Reports[0].Logical);
    }

    private static DirectoryScanner Reopen(IUserEnvironment environment) =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), new ScanEstimateCache(environment));
}
