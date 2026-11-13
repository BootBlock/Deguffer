using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// Several folders searched for files with the same content, as the Duplicates page searches them
/// (§7.4): through <see cref="DuplicateSearcher"/> with the real content reader and a checksum cache,
/// with each stage timed by the search itself (<see cref="DuplicateSearchTimings"/>). It reads files
/// and removes nothing.
///
/// <para><b>Its own checksums, never the app's.</b> The cache is kept in a
/// <see cref="ChecksumStoreFolder"/>, so a run neither reads what the person's searches kept nor
/// changes it (<see cref="CacheStart"/>). Cold, every run starts with that store gone, as a first search does. Warm, an
/// untimed search over the same folders fills it once, and every run loads it afresh, as the first
/// search after the app starts again does.</para>
///
/// <para><b>The store's write is not timed.</b> A search writes what it read behind itself, and the
/// next search waits for that write before it reads. Each run waits for it after its time instead,
/// so no run is charged for the last one's write.</para>
/// </summary>
internal sealed class DuplicatesRoute
{
    private readonly CandidateFinder _finder;
    private readonly VolumeMediaCache _media;
    private readonly ExploreActionPolicy _policy;
    private readonly DuplicateSearch _search;
    private readonly CacheStart _start;
    private readonly string _store;

    /// <summary>The cache the next run searches through, made afresh before each run.</summary>
    private ChecksumCache _cache;

    private bool _filled;

    /// <param name="policy">Explore's policy, whose refusals the search passes over.</param>
    /// <param name="start">What the checksum cache holds as each run starts.</param>
    /// <param name="store">The file the checksums are kept in, inside the route's own folder.</param>
    public DuplicatesRoute(
        CandidateFinder finder,
        VolumeMediaCache media,
        ExploreActionPolicy policy,
        DuplicateSearch search,
        CacheStart start,
        string store)
    {
        _finder = finder;
        _media = media;
        _policy = policy;
        _search = search;
        _start = start;
        _store = store;
        _cache = new ChecksumCache(store, TimeProvider.System);
    }

    public RouteRun Timed => new(RunAsync, BeforeAsync, AfterAsync);

    /// <summary>A search of <paramref name="folders"/> by their content, compared by <paramref name="checksum"/>, with what a search leaves out by default.</summary>
    public static DuplicateSearch Search(IReadOnlyList<string> folders, ChecksumAlgorithm checksum) =>
        new(MatchCriteria.Content, [.. folders.Select(folder => new SearchLocation(folder))], checksum);

    /// <summary>
    /// The route as the Duplicates page would search on this machine: its scanner, volumes, installed
    /// programs and folders, with the drives tuned as a search tunes them, and Explore's policy asked
    /// of this machine once, before the runs and outside their time, as the page asks it before each
    /// search.
    /// </summary>
    public static async Task<DuplicatesRoute> OnThisMachineAsync(
        IReadOnlyList<string> folders, ChecksumAlgorithm checksum, CacheStart start, string store, ScanTuner tuner, CancellationToken ct)
    {
        var media = new VolumeMediaCache(StorageQueries.Machine);
        var finder = new CandidateFinder(
            new ExploreScanner(VolumeMftSourceFactory.Default, tuning: tuner),
            VolumeInventory.Current,
            WindowsUninstallRegistry.Default,
            UserEnvironment.Current,
            SystemDirectories.Current,
            media);
        var search = Search(folders, checksum);

        // Asked about the locations as chosen and as each resolves, as the page's search asks.
        var located = finder.Locate(search);
        var scope = ProtectionScope.Within([.. folders, .. located.Locations.Select(location => location.Folder)], VolumeInventory.Current);
        var protections = await MachineProtections.ForThisMachineAsync(scope, ct).ConfigureAwait(false);

        return new DuplicatesRoute(finder, media, protections.Policy, search, start, store);
    }

    private async ValueTask BeforeAsync(CancellationToken ct)
    {
        if (_start is CacheStart.Cold)
        {
            File.Delete(LongPath.Extended(_store));
        }
        else if (!_filled)
        {
            var filling = new ChecksumCache(_store, TimeProvider.System);

            try
            {
                await SearchAsync(filling, timings: null, progress: null, ct).ConfigureAwait(false);
            }
            finally
            {
                // A search stopped while it reads still writes what it read, so the write is waited
                // for whichever way the search ended, before anything removes the store's folder.
                await filling.WhenStoredAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _filled = true;
        }

        _cache = new ChecksumCache(_store, TimeProvider.System);
    }

    private async ValueTask<RunTally> RunAsync(CancellationToken ct)
    {
        var timings = new DuplicateSearchTimings();
        var scanned = new LastScanCount();
        var result = await SearchAsync(_cache, timings, scanned, ct).ConfigureAwait(false);

        // A search stopped while it reads returns what it confirmed rather than throwing, and writes
        // what it read behind itself. A run stopped part-way is not a result, so it is thrown here for
        // the run loop to report, once that write has ended: the loop skips the wait after the run,
        // and the store's folder is removed next.
        if (ct.IsCancellationRequested)
        {
            await _cache.WhenStoredAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return new RunTally(
            scanned.Entries,
            timings.Ends.Bytes + timings.Whole.Bytes,
            Complete: Complete(result),
            Search: new SearchTimes(timings, result.Groups.Count));
    }

    private ValueTask AfterAsync() => new(_cache.WhenStoredAsync(CancellationToken.None));

    private Task<DuplicateSearchResult> SearchAsync(
        ChecksumCache cache, DuplicateSearchTimings? timings, IProgress<DuplicateSearchProgress>? progress, CancellationToken ct) =>
        new DuplicateSearcher(_finder, _media, cache).SearchAsync(_search, _policy, progress: progress, ct: ct, timings: timings);

    /// <summary>
    /// Whether the search answered for every file: every location searched and every place in it
    /// read, and no file left out because it went, changed or was refused while it was searched. A
    /// file left out for what it is, a link, an empty file or one not on this device, is a search
    /// answering as it should.
    /// </summary>
    private static bool Complete(DuplicateSearchResult result) =>
        !result.Stopped
        && result.Finding.Unsearched.Count == 0
        && result.Finding.Unread.Count == 0
        && result.LeftOut is { Gone: 0, Unidentified: 0, ReadFailed: 0, Changed: 0 };

    /// <summary>The scan's last count: the records its table reads read and the entries its walks listed, every pass added together.</summary>
    private sealed class LastScanCount : IProgress<DuplicateSearchProgress>
    {
        public long Entries { get; private set; }

        public void Report(DuplicateSearchProgress value)
        {
            if (value.Scan is { } scan)
            {
                Entries = scan.Done;
            }
        }
    }
}
