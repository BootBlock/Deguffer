using System.Diagnostics;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// A walk raced against a volume's index build, as Auto races them in a scan (<c>RouteRace</c>), and
/// each of the two alone, so one round says how much each slows the other.
///
/// <para><b>The order alternates.</b> The first round walks alone, builds alone, then races the two;
/// the next races first and walks alone last. A part that leaves the cache warm then favours the walk
/// alone in one round and the walk raced in the next. Each timed walk follows an untimed walk of its
/// own, so neither measures what a build before it left behind.</para>
///
/// <para><b>Started at the same moment.</b> The build runs on a thread of its own, as the scan's
/// does (<see cref="MftVolumeIndexCache"/>), and the walk on the calling thread. Both wait at a
/// barrier, so neither starts while the other is still being set up.</para>
///
/// <para>Before each part the garbage of the last is collected, so a part is not charged for a
/// collection its predecessor made necessary, and then the command given to run before each part
/// runs, if there is one (<see cref="PartCommand"/>).</para>
/// </summary>
internal sealed class RaceRoute
{
    private readonly Route _racer;
    private readonly Func<CancellationToken, RunTally> _walk;
    private readonly Func<CancellationToken, RunTally> _build;
    private readonly Action<CancellationToken>? _beforePart;
    private int _rounds;

    /// <param name="racer">The route <paramref name="walk"/> runs, named in the result.</param>
    /// <param name="walk">The walk, timed alone and raced.</param>
    /// <param name="build">The index build, timed alone and raced.</param>
    /// <param name="beforePart">Runs before each part, outside its time, or null for nothing.</param>
    public RaceRoute(Route racer, Func<CancellationToken, RunTally> walk, Func<CancellationToken, RunTally> build, Action<CancellationToken>? beforePart)
    {
        _racer = racer;
        _walk = walk;
        _build = build;
        _beforePart = beforePart;
    }

    public RouteRun Timed => new(Run);

    /// <summary>
    /// The route on this machine: <paramref name="racer"/> over <paramref name="folder"/>, raced
    /// against the index of <paramref name="buildDrive"/>, with <paramref name="beforePart"/> run
    /// before each part where it is given.
    /// </summary>
    public static RaceRoute On(
        IMftSourceFactory volumes,
        string folder,
        Route racer,
        WalkTuning walk,
        char buildDrive,
        TableTuning table,
        string? beforePart)
    {
        Func<CancellationToken, RunTally> walker = racer switch
        {
            Route.Walk => ct => WalkRoute.Run(folder, walk, ct),
            Route.Sources => ct => SourcesRoute.Run(folder, walk, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(racer), racer, "The race walks with the walk route or the sources route."),
        };

        return new RaceRoute(
            racer,
            walker,
            ct => TableRoutes.Run(Route.Index, volumes, $@"{buildDrive}:\", table, ct),
            beforePart is null ? null : PartCommand.For(beforePart).Run);
    }

    /// <returns>The walk alone's count, complete only where every part was.</returns>
    public RunTally Run(CancellationToken ct)
    {
        var reversed = _rounds++ % 2 == 1;

        RacePart walkAlone;
        RacePart buildAlone;
        (RacePart Walk, RacePart Build) raced;

        if (reversed)
        {
            raced = Raced(ct);
            buildAlone = Alone(_build, ct);
            walkAlone = WalkAlone(ct);
        }
        else
        {
            walkAlone = WalkAlone(ct);
            buildAlone = Alone(_build, ct);
            raced = Raced(ct);
        }

        var times = new RaceTimes(_racer, walkAlone, buildAlone, raced.Walk, raced.Build, reversed);

        return new RunTally(walkAlone.Items, BytesRead: 0, times.Complete, Race: times);
    }

    /// <summary>
    /// The walk alone, after an untimed walk of the same folder (see <see cref="Prime"/>).
    /// </summary>
    private RacePart WalkAlone(CancellationToken ct)
    {
        Prime(ct);

        return Alone(_walk, ct);
    }

    /// <summary>
    /// Walk the folder once, untimed, before a timed walk. A read through a volume's handle, which
    /// every build makes, slows the next walk of that volume by about half a second without reading
    /// the drive (#544), and the part before a timed walk can be a build. A scan's walk does not follow
    /// a build of its own volume, so neither timed walk may: each then measures only what the other
    /// part beside it costs.
    /// </summary>
    private void Prime(CancellationToken ct) => _walk(ct);

    private RacePart Alone(Func<CancellationToken, RunTally> part, CancellationToken ct)
    {
        Settle(ct);

        return Measure(part, ct);
    }

    private (RacePart Walk, RacePart Build) Raced(CancellationToken ct)
    {
        Prime(ct);
        Settle(ct);

        using var start = new Barrier(2);

        var building = WorkerThreads.RunAsync(
            WorkerThreads.Shared.Run,
            () =>
            {
                start.SignalAndWait(ct);
                return Measure(_build, ct);
            },
            ct);

        // A thread the system would not start faults the task before it returns, and would leave the
        // walk at the barrier for ever, so that is thrown here instead.
        if (building.IsFaulted)
        {
            building.GetAwaiter().GetResult();
        }

        start.SignalAndWait(ct);
        var walk = Measure(_walk, ct);

        return (walk, building.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Collect what the last part left, then run the command given: in that order, so a command that
    /// empties the cache runs last before the part.
    /// </summary>
    private void Settle(CancellationToken ct)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _beforePart?.Invoke(ct);
    }

    private static RacePart Measure(Func<CancellationToken, RunTally> part, CancellationToken ct)
    {
        var before = HeapCounters.Now();
        var started = Stopwatch.GetTimestamp();

        var tally = part(ct);

        var elapsed = Stopwatch.GetElapsedTime(started);

        return new RacePart(elapsed, HeapCounters.Now() - before, tally.Items, tally.Complete);
    }
}
