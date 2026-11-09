using System.Collections.Concurrent;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The names of a file with several are listed in the lanes the files are identified in, as a pass of
/// that stage (§7.4), because on a source tree of hard links one thread spent many seconds on them
/// after the page had said identifying was done (#708). The links are real, on the scratch drive, and
/// the listing is held and counted at the seam that reaches Windows.
/// </summary>
public sealed class CandidateNameListingTests : IDisposable
{
    private const byte Sata = 0x0B;
    private const byte Nvme = 0x11;
    private const int Length = 100;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// A solid-state disk lists several files' names at once, up to the bound and no further. Each
    /// listing is held until the bound's worth are under way, and then for a while longer, in which a
    /// listing past the bound would have started too.
    /// </summary>
    [Fact]
    public async Task ASolidStateDiskListsSeveralFilesNamesAtOnceUpToTheBound()
    {
        var linked = Linked(3 * ReadingLanes.SolidStateReaders);
        using var listing = new HeldListing(hold: TimeSpan.FromMilliseconds(20), gather: ReadingLanes.SolidStateReaders);

        var found = await Search(listing.Files, SolidState());

        Assert.False(
            listing.GatherTimedOut,
            $"The lane had {listing.PeakAtGatherLimit} of its {ReadingLanes.SolidStateReaders} readers listing at once after {HeldListing.GatherAtMost.TotalSeconds} s.");
        Assert.Equal(ReadingLanes.SolidStateReaders, listing.Peak);
        Assert.Equal(linked.Count, listing.Count);
        AssertEachHasBothNames(found, linked);
    }

    /// <summary>A spinning disk lists one file's names at a time, as it identifies one file at a time.</summary>
    [Fact]
    public async Task ASpinningDiskListsOneFilesNamesAtATime()
    {
        var linked = Linked(6);
        using var listing = new HeldListing(hold: TimeSpan.FromMilliseconds(5));

        var found = await Search(listing.Files, new FakeStorageQueries().Volume(_tree.Root, 1).Disk(1, Sata, seekPenalty: true));

        Assert.Equal(1, listing.Peak);
        Assert.Equal(linked.Count, listing.Count);
        AssertEachHasBothNames(found, linked);
    }

    /// <summary>
    /// The page is told how far the names have got, as identifying, so it never stands on a count that
    /// reads as finished while they are listed. Files with one name are not counted in that pass.
    /// </summary>
    [Fact]
    public async Task IdentifyingCountsTheFilesWhoseNamesAreListed()
    {
        var linked = Linked(5);
        using var listing = new HeldListing(hold: TimeSpan.Zero);
        ConcurrentQueue<DuplicateSearchProgress> reports = [];
        var identified = linked.Count + 1;

        await _tree.Finder(files: listing.Files, storage: SolidState()).FindAsync(
            Data(),
            _tree.Policy(),
            new CallbackProgress<DuplicateSearchProgress>(reports.Enqueue));

        IReadOnlyList<DuplicateSearchProgress> identifying = [.. reports.Where(report => report.Stage is DuplicateSearchStage.Identifying)];
        var listingStarts = identifying.ToList().LastIndexOf(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, 0, linked.Count));

        Assert.True(listingStarts > 0, "No pass over the files with several names was reported.");
        Assert.Equal(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, identified, identified), identifying[listingStarts - 1]);
        Assert.Equal(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, linked.Count, linked.Count), identifying[^1]);
    }

    /// <summary>A file whose names Windows will not list, or whose volume it will not name, is shown with the paths the search reached it by.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileWhoseNamesWillNotBeListedKeepsThePathsThatReachedIt(bool noMountPoint)
    {
        var first = _tree.File(Length, "Data", "first.bin");
        var second = HardLink.To(first, Path.Combine(_tree.Top, "Data", "Other", "second.bin"));
        _tree.File(Length, "Data", "copy.bin");
        HardLink.To(first, Path.Combine(_tree.Folder("Elsewhere"), "unsearched.bin"));
        var files = new FileInformation(
            FileInformation.Open,
            FileInformation.ReadIdentity,
            listNames: _ => noMountPoint ? [@"\never\used.bin"] : null,
            mountPointOf: path => noMountPoint ? null : MountPointLookup.Machine.MountPointOf(path));

        var found = await Search(files, new FakeStorageQueries());

        var names = Assert.Single(Assert.Single(found.Groups).Files, file => file.HasSeveralNames).Names;
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The volume's top is asked once for each searched location, however many of its files have
    /// several names, and each name is given from it. A file's own path is never asked about.
    /// </summary>
    [Fact]
    public async Task TheMountPointIsAskedOnceForEachLocation()
    {
        for (var file = 0; file < 4; file++)
        {
            HardLink.To(_tree.File(Length, "One", $"{file}.bin"), Path.Combine(_tree.Top, "Links", $"one{file}.bin"));
            HardLink.To(_tree.File(Length, "Two", $"{file}.bin"), Path.Combine(_tree.Top, "Links", $"two{file}.bin"));
        }

        ConcurrentQueue<string> asked = [];
        var files = new FileInformation(
            FileInformation.Open,
            FileInformation.ReadIdentity,
            listNames: _ => [@"\Folder\name.bin"],
            mountPointOf: path =>
            {
                asked.Enqueue(path);
                return path.EndsWith("One", StringComparison.Ordinal) ? @"Y:\" : @"Z:\";
            });

        var found = await _tree.Finder(files: files).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(Path.Combine(_tree.Top, "One")), new SearchLocation(Path.Combine(_tree.Top, "Two"))]),
            _tree.Policy());

        Assert.Equal([Path.Combine(_tree.Top, "One"), Path.Combine(_tree.Top, "Two")], asked.Order(StringComparer.Ordinal));
        Assert.All(Assert.Single(found.Groups).Files, file => Assert.Equal(
            [file.Path.StartsWith(Path.Combine(_tree.Top, "One"), StringComparison.Ordinal) ? @"Y:\Folder\name.bin" : @"Z:\Folder\name.bin"],
            file.Names));
    }

    /// <summary>
    /// A folder replaced by a link after the walk leads out of the location, where its mount point says
    /// nothing about the file's volume. The file is asked about by its own path, so no name is given
    /// from the wrong top and then judged as a place the file is not (§7.4). The move and the link
    /// are made as the file is about to be described.
    /// </summary>
    [Fact]
    public async Task AFileDescribedOutsideItsLocationHasItsOwnMountPointAsked()
    {
        var moving = _tree.File(Length, "Data", "Folder", "moved.bin");
        HardLink.To(moving, Path.Combine(_tree.Folder("Links"), "moved.bin"));
        _tree.File(Length, "Data", "copy.bin");
        var folder = Path.GetDirectoryName(moving)!;
        var away = Path.Combine(_tree.Folder("Away"), "Folder");
        var described = Path.Combine(away, "moved.bin");
        var moved = false;
        ConcurrentQueue<string> asked = [];
        var files = new FileInformation(
            (path, use) =>
            {
                if (use is HandleUse.Describe && !moved && LongPath.Display(path).Equals(moving, StringComparison.Ordinal))
                {
                    moved = true;
                    Directory.Move(folder, away);
                    Junction.ToDirectory(folder, away);
                }

                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity,
            listNames: _ => [@"\Folder\name.bin"],
            mountPointOf: path =>
            {
                asked.Enqueue(path);
                return path.Equals(described, StringComparison.Ordinal) ? @"Z:\" : @"Y:\";
            });

        var found = await Search(files, new FakeStorageQueries());

        Assert.True(moved);
        Assert.Contains(described, asked);
        Assert.Equal([@"Z:\Folder\name.bin"], Assert.Single(Assert.Single(found.Groups).Files, file => file.HasSeveralNames).Names);
    }

    /// <summary>
    /// Only what a group shows is listed: a file with several names and no match is never asked about,
    /// and a file reached by two of its names is asked about once.
    /// </summary>
    [Fact]
    public async Task OnlyTheFilesWithAMatchAreListedOnceEach()
    {
        var first = _tree.File(Length, "Data", "first.bin");
        HardLink.To(first, Path.Combine(_tree.Top, "Data", "Other", "second.bin"));
        _tree.File(Length, "Data", "copy.bin");
        var alone = _tree.File(Length + 1, "Data", "alone.bin");
        HardLink.To(alone, Path.Combine(_tree.Top, "Data", "Other", "alone again.bin"));
        using var listing = new HeldListing(hold: TimeSpan.Zero);

        await Search(listing.Files, new FakeStorageQueries());

        Assert.Equal(1, listing.Count);
    }

    /// <summary>A file whose two names put it in two groups of a search by name is still listed once.</summary>
    [Fact]
    public async Task AFileInTwoGroupsIsListedOnce()
    {
        var first = _tree.File(Length, "Data", "one.bin");
        HardLink.To(first, Path.Combine(_tree.Top, "Data", "Other", "two.bin"));
        _tree.File(Length, "Data", "Third", "one.bin");
        _tree.File(Length, "Data", "Fourth", "two.bin");
        using var listing = new HeldListing(hold: TimeSpan.Zero);

        var found = await _tree.Finder(files: listing.Files).FindAsync(
            new DuplicateSearch(MatchCriteria.Name, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
            _tree.Policy());

        Assert.Equal(2, found.Groups.Count);
        Assert.Equal(1, listing.Count);
    }

    /// <summary>§6.3, by the form of the path, from every reader's thread: a path cut at <c>MAX_PATH</c> lists another file's names or none.</summary>
    [Fact]
    public async Task EveryPathListedReachesWindowsInTheExtendedForm()
    {
        Linked(3 * ReadingLanes.SolidStateReaders);
        using var listing = new HeldListing(hold: TimeSpan.Zero);

        await Search(listing.Files, SolidState());

        Assert.NotEmpty(listing.Paths);
        Assert.All(listing.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>A search stopped while the names are listed stops, rather than listing the rest first.</summary>
    [Fact]
    public async Task ASearchStoppedWhileListingNamesStops()
    {
        Linked(8);
        using var stop = new CancellationTokenSource();
        using var listing = new HeldListing(hold: TimeSpan.Zero, onList: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tree.Finder(files: listing.Files, storage: new FakeStorageQueries()).FindAsync(
            Data(),
            _tree.Policy(),
            ct: stop.Token));

        Assert.Equal(1, listing.Count);
    }

    private static void AssertEachHasBothNames(CandidateFinding found, IReadOnlyList<(string Path, string Link)> linked)
    {
        var files = Assert.Single(found.Groups).Files;

        foreach (var (path, link) in linked)
        {
            var file = Assert.Single(files, candidate => candidate.Path == path);
            Assert.Equal(new[] { path, link }.Order(StringComparer.Ordinal), file.Names.Order(StringComparer.Ordinal));
        }

        Assert.Equal(linked.Count + 1, files.Count);
    }

    /// <summary>
    /// <paramref name="count"/> files that may match, each with a second name outside the search, and
    /// one with a single name, so a group is more than one file whatever <paramref name="count"/> is.
    /// </summary>
    private IReadOnlyList<(string Path, string Link)> Linked(int count)
    {
        _tree.File(Length, "Data", "single.bin");

        return [.. Enumerable.Range(0, count).Select(file =>
        {
            var path = _tree.File(Length, "Data", $"{file}.bin");

            return (path, HardLink.To(path, Path.Combine(_tree.Folder("Elsewhere"), $"{file}.bin")));
        })];
    }

    private DuplicateSearch Data() => new(MatchCriteria.Size, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]);

    private FakeStorageQueries SolidState() => new FakeStorageQueries().Volume(_tree.Root, 1).Disk(1, Nvme, seekPenalty: false);

    private Task<CandidateFinding> Search(FileInformation files, FakeStorageQueries storage) =>
        _tree.Finder(files: files, storage: storage).FindAsync(Data(), _tree.Policy());

    /// <summary>
    /// Lists each file's names as Windows does, holding each listing for <c>hold</c> and counting how
    /// many were under way at once at most. Where <c>gather</c> is more than one, each listing first
    /// waits until that many are under way, or until <see cref="GatherAtMost"/> passes, which
    /// <see cref="GatherTimedOut"/> records, so a listing moved back onto one thread fails rather than hangs.
    /// </summary>
    private sealed class HeldListing : IDisposable
    {
        public static readonly TimeSpan GatherAtMost = TimeSpan.FromSeconds(10);

        private readonly TimeSpan _hold;
        private readonly int _gather;
        private readonly Action? _onList;
        private readonly ManualResetEventSlim _gathered = new(initialState: false);
        private readonly ConcurrentQueue<string> _paths = [];

        private int _running;
        private int _peak;
        private int _count;
        private bool _gatherTimedOut;
        private int _peakAtGatherLimit;

        /// <param name="onList">Called as each listing starts, for a test that stops the search there.</param>
        public HeldListing(TimeSpan hold, int gather = 1, Action? onList = null)
        {
            _hold = hold;
            _gather = gather;
            _onList = onList;
            Files = new FileInformation(FileInformation.Open, FileInformation.ReadIdentity, List);
        }

        public FileInformation Files { get; }

        public int Peak => Volatile.Read(ref _peak);

        public int Count => Volatile.Read(ref _count);

        public bool GatherTimedOut => Volatile.Read(ref _gatherTimedOut);

        /// <summary>The most listings under way when the gather ran out of time, before the listings it let go could raise <see cref="Peak"/>.</summary>
        public int PeakAtGatherLimit => Volatile.Read(ref _peakAtGatherLimit);

        /// <summary>Each path in the form it reached Windows.</summary>
        public IReadOnlyCollection<string> Paths => _paths;

        public void Dispose() => _gathered.Dispose();

        private IReadOnlyList<string>? List(string extendedPath)
        {
            _paths.Enqueue(extendedPath);
            Interlocked.Increment(ref _count);
            _onList?.Invoke();

            var running = Interlocked.Increment(ref _running);
            RaisePeak(running);

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

                return FileInformation.ListNames(extendedPath);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private void RaisePeak(int running)
        {
            var seen = Volatile.Read(ref _peak);

            while (running > seen)
            {
                var previous = Interlocked.CompareExchange(ref _peak, running, seen);

                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
