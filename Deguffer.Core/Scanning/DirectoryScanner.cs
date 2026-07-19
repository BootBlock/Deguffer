using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The scanner §5.5 asks for: read the MFT, fall back to a bounded parallel walk where that is not
/// possible, cache across runs, and stream partial results rather than blocking.
///
/// Choosing between the two routes lives here and nowhere else. Providers, the planner and the
/// executor ask <see cref="IDirectoryScanner"/> for a size and are told how it was obtained; none
/// of them contains a branch on which strategy is in play, because the answer depends on the volume
/// and the process token rather than on anything about a cache (G1, G2).
/// </summary>
public sealed class DirectoryScanner : IDirectoryScanner
{
    private readonly MftVolumeIndexCache _volumes;
    private readonly ScanEstimateCache? _estimates;
    private readonly ParallelEnumerationScanner _fallback;
    private readonly Dictionary<(char Volume, string Name), IReadOnlyList<string>> _searches = [];
    private readonly Lock _searchGate = new();

    public DirectoryScanner(
        IMftSourceFactory? sources = null,
        ScanEstimateCache? estimates = null,
        ParallelEnumerationScanner? fallback = null)
    {
        _volumes = new MftVolumeIndexCache(sources ?? VolumeMftSourceFactory.Default);
        _estimates = estimates;
        _fallback = fallback ?? ParallelEnumerationScanner.Default;
    }

    /// <summary>
    /// The scanner the app runs with: real volumes, sizes remembered across runs.
    ///
    /// A single shared instance, following <c>ProcessRunner.Default</c> (G5). Sharing is not
    /// incidental here — the volume index is the entire cost of the fast path, and one scanner per
    /// provider would rebuild it three times over, making the MFT route slower than the walk it
    /// replaces.
    /// </summary>
    public static DirectoryScanner Default { get; } = CreateDefault(UserEnvironment.Current);

    public static DirectoryScanner CreateDefault(IUserEnvironment environment) =>
        new(VolumeMftSourceFactory.Default, new ScanEstimateCache(environment));

    public async ValueTask<ScanResult> MeasureAsync(
        string path,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // §5.5's "re-opening the tool is instant": show last run's figure straight away, then
        // correct it. Only ever reported through progress — the returned result is always freshly
        // measured, because callers subtract it to report reclaimed space.
        if (_estimates?.TryGet(path) is { } remembered)
        {
            progress?.Report(remembered);
        }

        var result = await MeasureFreshAsync(path, progress, ct).ConfigureAwait(false);

        _estimates?.Set(path, result.Size);
        return result;
    }

    /// <summary>
    /// Ask the volume index for directories by name, narrowed to <paramref name="root"/>.
    ///
    /// The narrowing is the consent model, not an optimisation. The index knows every directory on
    /// the volume, and a cheap answer must not turn into permission to act on something the user
    /// never approved — so anything outside the root is dropped here rather than left for a caller
    /// to remember.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!VolumePath.TryParse(root, out var volumeRoot))
        {
            return new((IReadOnlyList<string>?)null);
        }

        var index = _volumes.Get(volumeRoot.DriveLetter, out _, ct);

        if (index is null)
        {
            return new((IReadOnlyList<string>?)null);
        }

        var found = NamedDirectories(index, name, volumeRoot.DriveLetter, ct)
            .Where(path => IsUnder(path, root))
            .ToList();

        return new((IReadOnlyList<string>?)found);
    }

    /// <summary>
    /// Every directory of this name on the volume, as full paths, memoised for the life of the scan.
    ///
    /// The search is a linear pass over every record in the table, and discovery asks once per
    /// approved root — so without this a user with four source folders on one drive pays four
    /// complete passes over a multi-million-record array to answer the same question (G4). Cleared
    /// with the volume indexes it derives from, since a stale answer here is a stale deletion target.
    /// </summary>
    private IReadOnlyList<string> NamedDirectories(
        MftVolumeIndex index,
        string name,
        char driveLetter,
        CancellationToken ct)
    {
        var key = (driveLetter, name);

        lock (_searchGate)
        {
            if (_searches.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var prefix = driveLetter + @":\";
        var found = index.FindDirectoriesNamed(name, ct)
            .Select(components => prefix + string.Join(Path.DirectorySeparatorChar, components))
            .ToList();

        lock (_searchGate)
        {
            _searches[key] = found;
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits at or below <paramref name="root"/>. The separator is
    /// part of the comparison: without it <c>C:\Source</c> would claim <c>C:\SourceControl</c>.
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        var normalised = LongPath.Display(root).TrimEnd(Path.DirectorySeparatorChar);

        return path.Equals(normalised, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalised + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private ValueTask<ScanResult> MeasureFreshAsync(
        string path,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        if (!VolumePath.TryParse(path, out var volumePath))
        {
            return _fallback.Because(FallbackReason.VolumeNotAddressable).MeasureAsync(path, progress, ct);
        }

        var index = _volumes.Get(volumePath.DriveLetter, out var reason, ct);

        // A path the index cannot resolve is not the same as an empty one. It means the tree
        // changed under the index, or a component sits behind something the walk models and the
        // table does not — so ask the slow path rather than reporting zero, which would render as
        // "this cache is already clear" and quietly hide gigabytes.
        if (index?.TryMeasure(volumePath.Components) is { } size)
        {
            progress?.Report(size);
            return ValueTask.FromResult(ScanResult.Fast(size));
        }

        var fallbackReason = reason == FallbackReason.None ? FallbackReason.MasterFileTableUnreadable : reason;
        return _fallback.Because(fallbackReason).MeasureAsync(path, progress, ct);
    }

    /// <summary>
    /// Drop the volume indexes so the next pass reads the table afresh.
    ///
    /// Remembered estimates deliberately survive this. Invalidate runs at the *start* of a planning
    /// pass, and clearing them here would throw away the values that make the window populate
    /// instantly — the entire point of caching them. They need no explicit clearing in any case:
    /// every measurement overwrites its own entry with the fresh figure.
    /// </summary>
    public void Invalidate()
    {
        _volumes.Invalidate();

        // Derived from the indexes just dropped, so it goes with them. Unlike the remembered
        // estimates — which are a display convenience — a stale entry here would name a directory
        // that may no longer exist, and that is a deletion target rather than a number.
        lock (_searchGate)
        {
            _searches.Clear();
        }
    }
}
