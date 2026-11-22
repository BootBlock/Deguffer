using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Measures the bytes a tree holds that nothing outside it also holds: the sum over files whose
/// hard-link count is exactly one. That is the number an eviction of the tree's contents can
/// actually give back, and for a store that hard-links into every consumer — pnpm's, conda's — it
/// is the only defensible figure. Summing file lengths there counts each linked copy, and the
/// reported reclaim would be several times what the disk returns, which is §5.4's lesson on a
/// different subject.
///
/// <para><b>Two routes, one figure.</b> Where a volume's index is being built anyway, for the rows
/// measured beside this one, the index answers from memory (<see cref="LinkCounting.SoleName"/>),
/// raced against the walk while it is still read, as <see cref="DirectoryScanner"/> races it
/// (<see cref="VolumeRoutes"/>). Everywhere else <see cref="SoleLinkWalk"/> walks the tree and asks
/// each file its link count, which <c>GetFileInformationByHandleEx</c> answers at either privilege
/// level. The index counts a file's names from its record, 8.3 aliases left out, which is the count
/// Windows reports, so the figure of a tree does not depend on how the app was launched.</para>
///
/// <para><b>Never a build for this figure alone.</b> A build reads the whole volume, 3 to 5 s on a
/// system volume, where one store's walk costs tens of milliseconds warm, so a volume no other row
/// asks about is walked. For the same reason the figure never carries a fallback reason: no table was
/// fallen back from, and an offer of administrator rights beside it would change nothing
/// (<see cref="ScanResult.ByChoice"/>).</para>
///
/// <para>A file with several links all inside the tree is not counted, though deleting the whole
/// tree would free it. Telling that case apart means enumerating every link's name per multi-linked
/// file, for stores whose layout never produces it — content-addressed files link outward, not to
/// each other — so the cheap reading under-reports in the rare case rather than over-reporting in
/// any, which is the direction §5.4 allows.</para>
/// </summary>
public sealed class HardLinkAwareScanner : IDirectoryScanner
{
    /// <summary>The walk at the shipped values, for a scanner built outside the app (G5).</summary>
    public static readonly HardLinkAwareScanner Default = new(ScanTuner.Shipped);

    private readonly SoleLinkWalk _walk;

    /// <summary>The index or the walk, or null where no index is shared with this scanner.</summary>
    private readonly VolumeRoutes? _routes;

    /// <summary>The indexes read, kept for a pass while it holds this scanner.</summary>
    private readonly MftVolumeIndexCache? _volumes;

    public ScanTuner Tuning { get; }

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    public HardLinkAwareScanner(ScanTuner tuner)
        : this(tuner, FileInformation.Default, volumes: null)
    {
    }

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    /// <param name="files">
    /// How each file's link count and sizes are read, so a test or the benchmark can choose the
    /// route a file system would leave it.
    /// </param>
    internal HardLinkAwareScanner(ScanTuner tuner, FileInformation files)
        : this(tuner, files, volumes: null)
    {
    }

    /// <param name="tuner">What each walk runs with, and the route the user allows.</param>
    /// <param name="volumes">
    /// The indexes of the scanner whose rows are measured beside this one's. A build it has started
    /// answers this figure, and this scanner starts none. See <see cref="DirectoryScanner.HardLinks"/>.
    /// </param>
    internal HardLinkAwareScanner(ScanTuner tuner, MftVolumeIndexCache volumes)
        : this(tuner, FileInformation.Default, volumes)
    {
    }

    private HardLinkAwareScanner(ScanTuner tuner, FileInformation files, MftVolumeIndexCache? volumes)
    {
        Tuning = tuner;
        _walk = new SoleLinkWalk(tuner, files);
        _volumes = volumes;
        _routes = volumes is null ? null : new VolumeRoutes(volumes, tuner, _walk, LinkCounting.SoleName, volumes.Started);
    }

    public async ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ScanRequest[] asked = [new(path, bounds)];

        return (await MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0][0];
    }

    /// <summary>
    /// Every path the index answers in a lookup, and every other directory among
    /// <paramref name="asked"/> in one walk for each set of values, as <see cref="RootSetWalk"/> walks
    /// them, and every file in a read of its own.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        [.. (await MeasureEachAsync(asked, [keep], RunningTotal.Over(asked, progress), ct).ConfigureAwait(false)).Select(readings => readings[0])];

    /// <summary>One reading, tallied twice. See <see cref="IDirectoryScanner.MeasureAgedAsync"/>.</summary>
    public async ValueTask<AgedScan> MeasureAgedAsync(
        string path, MinimumAge age, IProgress<ScanSize>? progress = null, CancellationToken ct = default)
    {
        ScanRequest[] asked = [new(path)];

        return AgedScan.Of((await MeasureEachAsync(asked, AgedScan.Keeps(age), RunningTotal.Over(asked, progress), ct).ConfigureAwait(false))[0]);
    }

    /// <summary>
    /// Each of <paramref name="asked"/> measured once for each of <paramref name="keeps"/>, in their
    /// orders, by the index where one is shared with this scanner and being built, and by the walk
    /// otherwise.
    /// </summary>
    private Task<ScanResult[][]> MeasureEachAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge[] keeps,
        RunningPaths running,
        CancellationToken ct) =>
        _routes is { } routes
            ? routes.MeasureEachAsync(asked, keeps, running, ct)
            : _walk.MeasureEachAsync(asked, keeps, running, ct);

    /// <summary>Always null — this figure never searches. See <see cref="ParallelEnumerationScanner"/>.</summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>
    /// Straight to the walk, whatever the index holds, because a figure subtracted from an earlier one
    /// has to come from the disk. See <see cref="IDirectoryScanner.MeasureFromDiskAsync"/>.
    /// </summary>
    public async ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
        [.. (await _walk.MeasureEachAsync(asked, [MinimumAge.Off], RunningPaths.None, ct).ConfigureAwait(false)).Select(readings => readings[0])];

    /// <summary>
    /// Nothing to drop here. The indexes this scanner reads belong to the scanner that builds them,
    /// whose rows drop them at the start of every pass.
    /// </summary>
    public void Invalidate()
    {
    }

    /// <summary>
    /// Keep the indexes this scanner reads for the pass, so one built for the rows beside this one is
    /// still there when this row asks. Nothing where no index is shared.
    /// </summary>
    public PassHold Hold() => _volumes?.Hold() ?? PassHold.Nothing;
}
