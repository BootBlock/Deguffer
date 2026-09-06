using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Declares which directories are "in use", so a provider's veto is provable with no editor, no
/// build and no toolchain installed.
///
/// The real inspector is exercised against real live trees in <c>LiveTreeInspectorTests</c> —
/// nothing about the Restart Manager or the process table can be established with a fake. What this
/// proves is the other half: that a provider given a live directory refuses to target it, and that
/// it says so.
/// </summary>
public sealed class FakeLiveTreeInspector : ILiveTreeInspector
{
    private readonly HashSet<string> _live;
    private readonly bool _complete;

    public FakeLiveTreeInspector(bool complete, params string[] live)
    {
        _live = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        _complete = complete;
    }

    public FakeLiveTreeInspector(params string[] live) : this(complete: true, live) { }

    public static FakeLiveTreeInspector NothingLive => new();

    /// <summary>Nothing found, and nothing established either — the "could not tell" case.</summary>
    public static FakeLiveTreeInspector CannotTell => new(complete: false);

    public int InvalidateCount { get; private set; }

    /// <summary>What the provider asked about, so a test can assert the project folder was passed.</summary>
    public IReadOnlyList<LiveTreeQuery> Asked { get; private set; } = [];

    public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default)
    {
        Asked = candidates;

        return new LiveTreeFindings(
            [.. candidates.Where(c => _live.Contains(c.Directory))
                .Select(c => new LiveTree(c.Directory, ["a test says something is using it"]))],
            _complete);
    }

    /// <summary>
    /// The declared live directories that sit below one of <paramref name="directories"/>.
    ///
    /// Matched by containment rather than by exact name, so a test declares the child path it wants
    /// spared and this stands in for the process table that would have named it.
    /// </summary>
    public LiveTreeFindings FindLiveChildren(
        IReadOnlyList<string> directories,
        CancellationToken ct = default)
    {
        return new LiveTreeFindings(
            [.. _live
                .Where(child => directories.Any(root => LongPath.Contains(root, child)))
                .Select(child => new LiveTree(child, ["a test says something is using it"]))],
            _complete);
    }

    public void Invalidate() => InvalidateCount++;
}
