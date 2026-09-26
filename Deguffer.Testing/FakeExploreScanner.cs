using Deguffer.Core.Exploring;

namespace Deguffer.Testing;

/// <summary>
/// A scanner whose scans a test runs by hand: each call waits until the test reports progress into
/// it, finishes it or fails it, so a snapshot can be made to land exactly where the case needs it.
/// </summary>
public sealed class FakeExploreScanner : IExploreScanner
{
    private readonly List<FakeExploreScan> _scans = [];

    /// <summary>Every scan asked for, oldest first.</summary>
    public IReadOnlyList<FakeExploreScan> Scans => _scans;

    /// <summary>The scan asked for most recently.</summary>
    public FakeExploreScan Current => _scans[^1];

    public ValueTask<ExploreScan> ScanAsync(
        string root,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scan = new FakeExploreScan(root, progress, ct);
        _scans.Add(scan);

        return new ValueTask<ExploreScan>(scan.Result);
    }
}
