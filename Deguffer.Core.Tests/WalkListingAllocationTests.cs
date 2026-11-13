using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A walk reads hundreds of thousands of directories, so what it allocates to read one listing is
/// garbage made hundreds of thousands of times on the walk's workers (G4). A listing enumerated
/// through an interface boxes an enumerator for each list, and one enumerated as a span boxes none.
/// </summary>
public sealed class WalkListingAllocationTests
{
    private const int Folders = 1_000;

    private const int FilesInEach = 4;

    private const long FileLength = 100;

    /// <summary>
    /// A measurement of 1,000 directories reads each listing without boxing an enumerator for it. What
    /// is counted is the worker's own allocation from the end of one listing to the start of its next:
    /// the measurement's reading of the listing, the child paths it descends into, which it keeps, and
    /// the walk's own work for each directory.
    /// The fake listing's own work is left out, and so is every other test running at the same time,
    /// which a process-wide count would take in.
    /// </summary>
    [Fact]
    public async Task AMeasurementReadsEachListingWithoutAllocatingForIt()
    {
        // The drive's root, which is there on every machine, so each child's path is the same length on
        // every machine too: the walk keeps a child's path, and a temporary folder's length depends on
        // the account. Nothing below it is listed from the disk.
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var counter = new BetweenListings();
        var scanner = new ParallelEnumerationScanner(OneThread(), counter.Wrap(FakeTree(root)), TimeProvider.System);

        // Once first, so what is counted is the walk rather than the first call's compilation.
        await scanner.MeasureAsync(root);

        // The least of three walks, so a one-off cost a walk pays, spread over 1,000 directories, cannot
        // pass for a cost of each.
        var perFolder = long.MaxValue;

        for (var walk = 0; walk < 3; walk++)
        {
            counter.Reset();

            var result = await scanner.MeasureAsync(root);

            Assert.Equal(Folders * FilesInEach * FileLength, result.Size.Reclaimable);

            var (bytes, intervals) = counter.Read();
            Assert.True(intervals >= Folders, $"Only {intervals:N0} intervals between listings were counted.");
            perFolder = Math.Min(perFolder, bytes / intervals);
        }

        // Measured at 249 bytes a directory. Each enumerator boxed for a listing adds about 88 more, and
        // a measurement enumerates each listing twice, once in the walk and once to total it, so the
        // bound sits below one box.
        Assert.True(perFolder < 300, $"The walk allocated {perFolder:N0} bytes a directory between listings.");
    }

    /// <summary>
    /// A walk on one thread. Where several share a walk, whether a child is handed to another worker
    /// depends on how the threads were scheduled, and a hand-off costs what reading a listing does not.
    /// </summary>
    private static ScanTuner OneThread() => new(
        new FakePreferences(AppPreferences.Default with
        {
            Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: 1)),
        }),
        new VolumeMediaCache(new FakeStorageQueries()),
        new FakeVolumeInventory());

    /// <summary>
    /// A tree of <see cref="Folders"/> directories below <paramref name="root"/>, each holding
    /// <see cref="FilesInEach"/> files, listed without the disk.
    /// </summary>
    private static ListDirectory FakeTree(string root)
    {
        string[] folderNames = [.. Enumerable.Range(0, Folders).Select(folder => $"folder-{folder:D4}")];

        return (directory, request, into) =>
        {
            if (LongPath.Display(directory).Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in folderNames)
                {
                    into.Add(new WalkEntry(directory, request.NameOf(name, isDirectory: true), FileAttributes.Directory, 0, 0, 0));
                }
            }
            else
            {
                for (var file = 0; file < FilesInEach; file++)
                {
                    into.Add(new WalkEntry(directory, request.NameOf($"{file}.bin", isDirectory: false), FileAttributes.Archive, FileLength, 0, 0));
                }
            }

            return ListingEnd.Listed;
        };
    }

    /// <summary>
    /// Counts what each thread allocates between the end of one listing and the start of its next,
    /// with the thread's own counter, which no other thread moves.
    /// </summary>
    private sealed class BetweenListings
    {
        private readonly ThreadLocal<long?> _listingEnded = new();

        private long _bytes;

        private int _intervals;

        private int _generation;

        private readonly ThreadLocal<int> _seenGeneration = new();

        public ListDirectory Wrap(ListDirectory list) => (directory, request, into) =>
        {
            var started = GC.GetAllocatedBytesForCurrentThread();
            var generation = Volatile.Read(ref _generation);

            if (_seenGeneration.Value == generation && _listingEnded.Value is { } ended)
            {
                Interlocked.Add(ref _bytes, started - ended);
                Interlocked.Increment(ref _intervals);
            }

            var end = list(directory, request, into);

            _seenGeneration.Value = generation;
            _listingEnded.Value = GC.GetAllocatedBytesForCurrentThread();
            return end;
        };

        /// <summary>Forget what was counted, and every listing a thread ended before now.</summary>
        public void Reset()
        {
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _bytes, 0);
            Interlocked.Exchange(ref _intervals, 0);
        }

        public (long Bytes, int Intervals) Read() => (Interlocked.Read(ref _bytes), Volatile.Read(ref _intervals));
    }
}
