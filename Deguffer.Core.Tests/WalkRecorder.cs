using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// An <see cref="ExploreScanner.Walker"/> that reads nothing. It records each folder it is asked to
/// walk and answers with that folder alone, so a scan of <see cref="DriveLetters.Faked"/> or of a
/// share only a test names reaches neither the machine's own drives nor the network.
/// </summary>
internal sealed class WalkRecorder
{
    private readonly Lock _gate = new();
    private readonly List<string> _walked = [];

    /// <summary>Every folder a walk was asked of, in the form it arrived in.</summary>
    public IReadOnlyList<string> Walked
    {
        get
        {
            lock (_gate)
            {
                return [.. _walked];
            }
        }
    }

    public ExploreTree Walk(
        string root,
        ScanTuner tuning,
        IOccupancyProbe probe,
        IProgress<ExploreProgress>? progress,
        TimeProvider clock,
        CancellationToken ct,
        LeavesUnlisted? unlisted,
        SnapshotAssembly? snapshotAssembly)
    {
        lock (_gate)
        {
            _walked.Add(root);
        }

        return new ExploreTreeBuilder(LongPath.Display(root)).Build(ExploreChildOrder.BySize);
    }
}
