using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A scanner whose volume index can be present or absent on demand.
///
/// The real one answers <see cref="IDirectoryScanner.TryFindDirectoriesNamedAsync"/> only when
/// Deguffer is elevated, so without this the fallback path would be tested on an unelevated machine
/// and the indexed path on an elevated one — the two would never both be covered in a single run,
/// and which one a test exercised would depend on how the test host happened to be launched.
///
/// Measurement is delegated to the real walk rather than stubbed, so sizes in these tests are
/// genuinely measured off disk.
/// </summary>
public sealed class FakeDirectoryScanner(IReadOnlyList<string>? indexed = null) : IDirectoryScanner
{
    /// <summary>How many times discovery asked the index. Zero proves a test drove the walk.</summary>
    public int FindCalls { get; private set; }

    public int InvalidateCount { get; private set; }

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        ParallelEnumerationScanner.Default.MeasureAsync(path, progress, ct);

    /// <summary>
    /// Null when this fake has no index, matching the contract the real scanner uses to tell the
    /// caller to walk instead. Results are narrowed to <paramref name="root"/> exactly as the real
    /// one narrows them, because that narrowing is the consent model rather than an optimisation.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default)
    {
        FindCalls++;

        if (indexed is null)
        {
            return new((IReadOnlyList<string>?)null);
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return new((IReadOnlyList<string>?)indexed
            .Where(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))
                    .Equals(name, StringComparison.OrdinalIgnoreCase)
                && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    public void Invalidate() => InvalidateCount++;
}
