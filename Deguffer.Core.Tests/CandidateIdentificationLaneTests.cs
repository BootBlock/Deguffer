using System.Collections.Concurrent;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// The files that may match are identified in lanes, one a physical disk, bounded by what the disk
/// is, as their content is read (§7.4). The opens are held and counted on the scratch drive, whose
/// disks are described by the fakes.
/// </summary>
public sealed class CandidateIdentificationLaneTests : IDisposable
{
    private const byte Sata = 0x0B;
    private const byte Nvme = 0x11;
    private const int Length = 100;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// A solid-state disk is asked about several files at once, up to the bound and no further. Each
    /// open is held until the bound's worth are under way, and then for a while longer, in which an
    /// open past the bound would have started too.
    /// </summary>
    [Fact]
    public async Task ASolidStateDiskIdentifiesSeveralFilesAtOnceUpToTheBound()
    {
        var candidates = Candidates(3 * ReadingLanes.SolidStateReaders);
        using var opens = new HeldOpens(candidates, hold: TimeSpan.FromMilliseconds(20), gather: ReadingLanes.SolidStateReaders);

        var found = await Search(opens, new FakeStorageQueries().Volume(_tree.Root, 1).Disk(1, Nvme, seekPenalty: false));

        Assert.False(
            opens.GatherTimedOut,
            $"The lane had {opens.PeakAtGatherLimit} of its {ReadingLanes.SolidStateReaders} readers running at once after {HeldOpens.GatherAtMost.TotalSeconds} s.");
        Assert.Equal(ReadingLanes.SolidStateReaders, opens.Peak);
        Assert.Equal(candidates.Count, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>
    /// A spinning disk is asked about one file at a time, so its file records are never read out of
    /// order by several readers at once.
    /// </summary>
    [Fact]
    public async Task ASpinningDiskIdentifiesOneFileAtATime()
    {
        var candidates = Candidates(8);
        using var opens = new HeldOpens(candidates, hold: TimeSpan.FromMilliseconds(5));

        var found = await Search(opens, new FakeStorageQueries().Volume(_tree.Root, 1).Disk(1, Sata, seekPenalty: true));

        Assert.Equal(1, opens.Peak);
        Assert.Equal(candidates.Count, opens.Count);
        Assert.Equal(candidates.Count, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>A disk Windows would not describe may spin, so its files are identified by one reader.</summary>
    [Fact]
    public async Task ADiskWindowsWillNotDescribeIdentifiesOneFileAtATime()
    {
        using var opens = new HeldOpens(Candidates(6), hold: TimeSpan.FromMilliseconds(5));

        await Search(opens, new FakeStorageQueries());

        Assert.Equal(1, opens.Peak);
    }

    /// <summary>
    /// Every path that reaches Windows to be identified is in the extended form (§6.3), from every
    /// reader's thread: a path cut at <c>MAX_PATH</c> names another file or none.
    /// </summary>
    [Fact]
    public async Task EveryPathIdentifiedReachesWindowsInTheExtendedForm()
    {
        using var opens = new HeldOpens(Candidates(3 * ReadingLanes.SolidStateReaders), hold: TimeSpan.Zero);

        await Search(opens, new FakeStorageQueries().Volume(_tree.Root, 1).Disk(1, Nvme, seekPenalty: false));

        Assert.NotEmpty(opens.Paths);
        Assert.All(opens.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// The stage says how far it has got, from none of the files that may match to all of them, so
    /// the page never stands on the last count of the scan. A file that shares nothing is not one of
    /// them, and is never opened.
    /// </summary>
    [Fact]
    public async Task IdentifyingSaysHowFarItHasGotOverTheFilesThatMayMatch()
    {
        var candidates = Candidates(5);
        var alone = _tree.File(Length + 1, "Data", "alone.bin");
        using var opens = new HeldOpens([.. candidates, alone], hold: TimeSpan.Zero);
        ConcurrentQueue<DuplicateSearchProgress> reports = [];

        await _tree.Finder(files: opens.Files, storage: new FakeStorageQueries()).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
            _tree.Policy(),
            new CallbackProgress<DuplicateSearchProgress>(reports.Enqueue));

        IReadOnlyList<DuplicateSearchProgress> identifying = [.. reports.Where(report => report.Stage is DuplicateSearchStage.Identifying)];
        Assert.Equal(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, 0, candidates.Count), identifying[0]);
        Assert.Equal(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, candidates.Count, candidates.Count), identifying[^1]);
        Assert.Equal(candidates.Count, opens.Count);
    }

    /// <summary>A search stopped while its files are identified stops, rather than identifying the rest first.</summary>
    [Fact]
    public async Task ASearchStoppedWhileIdentifyingStops()
    {
        using var stop = new CancellationTokenSource();
        using var opens = new HeldOpens(Candidates(8), hold: TimeSpan.Zero, onOpen: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tree.Finder(files: opens.Files, storage: new FakeStorageQueries()).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
            _tree.Policy(),
            ct: stop.Token));

        Assert.Equal(1, opens.Count);
    }

    private IReadOnlyList<string> Candidates(int count) =>
        [.. Enumerable.Range(0, count).Select(i => _tree.File(Length, "Data", $"{i}.bin"))];

    private Task<CandidateFinding> Search(HeldOpens opens, FakeStorageQueries storage) =>
        _tree.Finder(files: opens.Files, storage: storage).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
            _tree.Policy());

    /// <summary>
    /// Opens each path as Windows does, and holds every open that describes one of
    /// <paramref name="watched"/> for <paramref name="hold"/>, counting how many were under way at
    /// once at most. Where <paramref name="gather"/> is more than one, each such open first waits until
    /// that many are under way, or until <see cref="GatherAtMost"/> passes, which
    /// <see cref="GatherTimedOut"/> records, so how soon each reader's thread starts never decides
    /// how many are seen at once.
    /// </summary>
    private sealed class HeldOpens : IDisposable
    {
        public static readonly TimeSpan GatherAtMost = TimeSpan.FromSeconds(10);

        private readonly HashSet<string> _watched;
        private readonly TimeSpan _hold;
        private readonly int _gather;
        private readonly Action? _onOpen;
        private readonly ManualResetEventSlim _gathered = new(initialState: false);
        private readonly ConcurrentQueue<string> _paths = [];

        private int _running;
        private int _peak;
        private int _count;
        private bool _gatherTimedOut;
        private int _peakAtGatherLimit;

        /// <param name="onOpen">Called as each watched open starts, for a test that stops the search there.</param>
        public HeldOpens(IEnumerable<string> watched, TimeSpan hold, int gather = 1, Action? onOpen = null)
        {
            _watched = new HashSet<string>(watched, StringComparer.OrdinalIgnoreCase);
            _hold = hold;
            _gather = gather;
            _onOpen = onOpen;
            Files = new FileInformation(Open, FileInformation.ReadIdentity);
        }

        public FileInformation Files { get; }

        public void Dispose() => _gathered.Dispose();

        public int Peak => Volatile.Read(ref _peak);

        public int Count => Volatile.Read(ref _count);

        public bool GatherTimedOut => Volatile.Read(ref _gatherTimedOut);

        /// <summary>The most opens under way when the gather ran out of time, before the opens it let go could raise <see cref="Peak"/>.</summary>
        public int PeakAtGatherLimit => Volatile.Read(ref _peakAtGatherLimit);

        /// <summary>Each watched path in the form it reached Windows.</summary>
        public IReadOnlyCollection<string> Paths => _paths;

        private SafeFileHandle Open(string extendedPath, HandleUse use)
        {
            if (use is not HandleUse.Describe || !_watched.Contains(LongPath.Display(extendedPath)))
            {
                return FileInformation.Open(extendedPath, use);
            }

            _paths.Enqueue(extendedPath);
            Interlocked.Increment(ref _count);
            _onOpen?.Invoke();

            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _peak, running);

            try
            {
                if (running >= _gather)
                {
                    _gathered.Set();
                }
                else if (_gather > 1 && !_gathered.Wait(GatherAtMost))
                {
                    Volatile.Write(ref _peakAtGatherLimit, Peak);
                    Volatile.Write(ref _gatherTimedOut, true);
                    _gathered.Set();
                }

                Thread.Sleep(_hold);

                return FileInformation.Open(extendedPath, use);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var seen = Volatile.Read(ref target);

            while (value > seen)
            {
                var previous = Interlocked.CompareExchange(ref target, value, seen);

                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
