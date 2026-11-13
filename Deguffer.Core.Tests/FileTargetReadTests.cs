using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the walk reads each path it is handed before any walk starts: one attribute read a path,
/// whichever kind it is, several paths at once, and the file measured from that read.
///
/// <para>The reads go through the scanner's read seam, which wraps the real read for a test on the
/// disk and stands in for it for a test of how the reads are scheduled. Each wait is bounded, so a
/// run that reads one path at a time fails on its assertion rather than hanging.</para>
/// </summary>
public sealed class FileTargetReadTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A file costs one read, as a directory and an absent path do. Asking for a directory first read
    /// a file once to learn none was there, and again for its length and times.
    /// </summary>
    [Fact]
    public async Task EachPathIsReadOnceWhateverItIs()
    {
        var file = _temp.CreateFile(8192, "dumps", "MEMORY.DMP");
        var directory = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "blob.bin");
        var absent = Path.Combine(_temp.Path, "absent");

        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scanner = Reading((string path, out EntryReading reading) =>
        {
            lock (reads)
            {
                reads[path] = reads.GetValueOrDefault(path) + 1;
            }

            return LongPath.ReadEntry(path, out reading);
        });

        var measured = await scanner.MeasureManyAsync([new(file), new(directory), new(absent)]);

        Assert.Equal([8192, 4096, 0], measured.Select(result => result.Size.Reclaimable));
        Assert.Equal(ScanStrategy.DirectRead, measured[0].Strategy);
        Assert.Equal(1, reads[file]);
        Assert.Equal(1, reads[directory]);
        Assert.Equal(1, reads[absent]);
    }

    /// <summary>
    /// Many files are read several at once. Each read waits until another has started, so read one at
    /// a time the first would wait for a second that never starts, and both would time out.
    /// </summary>
    [Fact]
    public async Task ReadsOfSeveralPathsOverlap()
    {
        using var both = new Barrier(2);

        string[] paths = [@"\\volume.test\logs\first.log", @"\\volume.test\logs\second.log"];
        var met = new bool[paths.Length];

        var scanner = Reading((string path, out EntryReading reading) =>
        {
            met[Array.IndexOf(paths, path)] = both.SignalAndWait(Patience);
            reading = new EntryReading(FileAttributes.Normal, 100, 0);
            return PathPresence.Present;
        }, TunedTo(2));

        var measured = await scanner.MeasureManyAsync([.. paths.Select(path => new ScanRequest(path))]);

        Assert.Equal([true, true], met);
        Assert.Equal([100, 100], measured.Select(result => result.Size.Reclaimable));
    }

    /// <summary>
    /// The answers keep the order asked, though the reads finish in reverse: the planner pairs each
    /// answer with its path by position.
    /// </summary>
    [Fact]
    public async Task AnswersKeepTheOrderAskedWhenTheReadsFinishInReverse()
    {
        string[] paths = [@"\\volume.test\logs\a.log", @"\\volume.test\logs\b.log", @"\\volume.test\logs\c.log"];
        var finished = paths.Select(_ => new ManualResetEventSlim()).ToArray();

        try
        {
            var scanner = Reading((string path, out EntryReading reading) =>
            {
                var index = Array.IndexOf(paths, path);

                if (index < paths.Length - 1)
                {
                    finished[index + 1].Wait(Patience);
                }

                finished[index].Set();
                reading = new EntryReading(FileAttributes.Normal, 10 * (index + 1), 0);
                return PathPresence.Present;
            }, TunedTo(paths.Length));

            var measured = await scanner.MeasureManyAsync([.. paths.Select(path => new ScanRequest(path))]);

            Assert.Equal([10, 20, 30], measured.Select(result => result.Size.Reclaimable));
        }
        finally
        {
            foreach (var done in finished)
            {
                done.Dispose();
            }
        }
    }

    /// <summary>
    /// The guard judges a file from the read that sized it, and says so for the plan: a recent file is
    /// kept, an old one is not, and neither is judged where no guard was asked for.
    /// </summary>
    [Fact]
    public async Task SaysWhetherTheGuardKeepsAFileItMeasured()
    {
        var recent = _temp.CreateFile(4096, "logs", "recent.log");
        var old = TempDirectory.Age(_temp.CreateFile(4096, "logs", "old.log"), TimeSpan.FromDays(30));
        var guard = MinimumAge.WithinHours(8, DateTime.UtcNow);

        var guarded = await ParallelEnumerationScanner.Default.MeasureManyAsync([new(recent), new(old)], guard);
        var unguarded = await ParallelEnumerationScanner.Default.MeasureAsync(recent);

        Assert.Equal((true, true, 0L), (guarded[0].FileKept, guarded[0].WithheldRecent, guarded[0].Size.Reclaimable));
        Assert.Equal((false, false, 4096L), (guarded[1].FileKept, guarded[1].WithheldRecent, guarded[1].Size.Reclaimable));
        Assert.False(unguarded.FileKept);
    }

    /// <summary>
    /// A file link measures nothing, as it always has, because its length is its own and its target is a
    /// tree nobody looked inside. The guard still judges it by the link's own times, as the removal
    /// does, so a link written just now to an old file is kept.
    /// </summary>
    [Fact]
    public async Task JudgesAFileLinkByItsOwnTimesAndMeasuresItAsNothing()
    {
        var target = TempDirectory.Age(_temp.CreateFile(8192, "elsewhere", "old.bin"), TimeSpan.FromDays(30));
        var link = Path.Combine(_temp.CreateDirectory("logs"), "recent-link.bin");
        SymbolicLink.ToFile(link, target);

        var guarded = await ParallelEnumerationScanner.Default.MeasureAsync(link, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal(0, guarded.Size.Reclaimable);
        Assert.False(guarded.WithheldRecent);
        Assert.True(guarded.FileKept);
        Assert.Equal(ScanStrategy.ParallelEnumeration, guarded.Strategy);
    }

    /// <summary>
    /// A file whose attributes Windows will not give is answered as a refused directory is: not
    /// described, which the plan says, rather than an absence measured as zero.
    /// </summary>
    [Fact]
    public async Task AnswersARefusedFileAsNotDescribed()
    {
        var file = _temp.CreateFile(4096, "locked", "dump.dmp");
        using var denied = DeniedDirectory.WithUnreadableFile(file);

        var measured = await ParallelEnumerationScanner.Default.MeasureAsync(file, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal(RootReach.NotDescribed, measured.Root);
        Assert.Equal(0, measured.Size.Reclaimable);
        Assert.Null(measured.FileKept);
    }

    /// <summary>
    /// A mail store is left out, named, and given no verdict: the plan withholds it as a store before
    /// any guard is asked, so a verdict about its age would answer nobody.
    /// </summary>
    [Fact]
    public async Task GivesAMailStoreNoVerdict()
    {
        var store = _temp.CreateFile(8192, "Documents", "archive.pst");

        var measured = await ParallelEnumerationScanner.Default.MeasureAsync(store, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal([store], measured.MailStores);
        Assert.False(measured.WithheldRecent);
        Assert.Null(measured.FileKept);
    }

    /// <summary>
    /// The link-aware scanner judges a file by the time its own probe read, once for each guard, and
    /// gives the plan the same verdict.
    /// </summary>
    [Fact]
    public async Task TheLinkAwareScannerJudgesAFileFromTheReadThatFoundIt()
    {
        var recent = _temp.CreateFile(4096, "store", "recent.bin");
        var guard = MinimumAge.WithinHours(8, DateTime.UtcNow);

        var aged = await HardLinkAwareScanner.Default.MeasureAgedAsync(recent, guard);

        Assert.Equal((false, false), (aged.Whole.FileKept, aged.Whole.WithheldRecent));
        Assert.Equal((true, true), (aged.Aged.FileKept, aged.Aged.WithheldRecent));
        Assert.Equal(4096, aged.Whole.Size.Logical);
        Assert.Equal(0, aged.Aged.Size.Logical);
    }

    private static ParallelEnumerationScanner Reading(ReadEntry read, ScanTuner? tuning = null) =>
        new(tuning ?? ScanTuner.Shipped, DirectoryQuery.List, TimeProvider.System, read);

    /// <summary>A walk of <paramref name="threads"/> on every volume: the tuner knows no drive's kind, so each is unknown.</summary>
    private static ScanTuner TunedTo(int threads) => new(new FakePreferences(AppPreferences.Default with
    {
        Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: threads)),
    }));
}
