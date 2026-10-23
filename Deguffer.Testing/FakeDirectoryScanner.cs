using System.Collections.Concurrent;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// A scanner whose volume index can be present or absent on demand.
///
/// The real one answers <see cref="IDirectoryScanner.TryFindDirectoriesAsync"/> only when
/// Deguffer is elevated, so without this the fallback path would be tested on an unelevated machine
/// and the indexed path on an elevated one — the two would never both be covered in a single run,
/// and which one a test exercised would depend on how the test host happened to be launched.
///
/// Measurement is delegated to the real walk rather than stubbed, so sizes in these tests are
/// genuinely measured off disk.
/// </summary>
public sealed class FakeDirectoryScanner(IReadOnlyList<string>? indexed = null) : IDirectoryScanner
{
    private readonly ConcurrentQueue<string> _measured = new();
    private readonly ConcurrentQueue<(string Root, DirectorySearch Search)> _asked = new();
    private int _holds;
    private int _taken;

    /// <summary>How many times discovery asked the index. Zero proves a test drove the walk.</summary>
    public int FindCalls => _asked.Count;

    /// <summary>Every root the index was asked about, with the search it was asked to run there.</summary>
    public IReadOnlyCollection<(string Root, DirectorySearch Search)> Asked => _asked;

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Every path measured, by either route. What proves a provider never measured a path it
    /// promises to leave alone, which a size or a survival check cannot show.
    /// </summary>
    public IReadOnlyCollection<string> Measured => _measured;

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        _measured.Enqueue(path);
        return ParallelEnumerationScanner.Default.MeasureAsync(path, keep, bounds, progress, ct);
    }

    /// <summary>How many lists were measured, each in one call.</summary>
    public int MeasureManyCalls => Volatile.Read(ref _measureManyCalls);

    private int _measureManyCalls;

    public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _measureManyCalls);

        foreach (var question in asked)
        {
            _measured.Enqueue(question.Path);
        }

        return ParallelEnumerationScanner.Default.MeasureManyAsync(asked, keep, ct);
    }

    /// <summary>One walk, recorded once, as the real scanner makes it.</summary>
    public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default)
    {
        _measured.Enqueue(path);
        return ParallelEnumerationScanner.Default.MeasureAgedAsync(path, age, ct);
    }

    /// <summary>How many lists were read from the disk, each in one call.</summary>
    public int MeasureFromDiskCalls => Volatile.Read(ref _measureFromDiskCalls);

    private int _measureFromDiskCalls;

    /// <summary>This fake keeps no snapshot, so the two routes are the same walk.</summary>
    public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _measureFromDiskCalls);

        foreach (var question in asked)
        {
            _measured.Enqueue(question.Path);
        }

        return ParallelEnumerationScanner.Default.MeasureFromDiskAsync(asked, ct);
    }

    /// <summary>
    /// Null when this fake has no index, matching the contract the real scanner uses to tell the
    /// caller to walk instead. Otherwise the indexed paths <paramref name="search"/> finds below
    /// <paramref name="root"/>, judged as the real one judges them: narrowed to the root, because
    /// that is the consent model rather than an optimisation, and stopped where the search stops.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default)
    {
        _asked.Enqueue((root, search));

        if (indexed is null)
        {
            return new((IReadOnlyList<string>?)null);
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return new((IReadOnlyList<string>?)indexed
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(p => SourceTreeBoundary.IsInsideTheSearch(p, root, search)
                && search.Meet(Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))) is SearchStep.Found)
            .Order(StringComparer.Ordinal)
            .ToList());
    }

    public void Invalidate() => InvalidateCount++;

    /// <summary>How many holds are standing, so a test can see that a pass gave back each one it took.</summary>
    public int HoldsStanding => Volatile.Read(ref _holds);

    /// <summary>How many holds were ever taken.</summary>
    public int HoldsTaken => Volatile.Read(ref _taken);

    public PassHold Hold()
    {
        Interlocked.Increment(ref _taken);
        Interlocked.Increment(ref _holds);

        return new PassHold(() => Interlocked.Decrement(ref _holds));
    }
}
