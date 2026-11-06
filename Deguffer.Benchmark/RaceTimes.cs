namespace Deguffer.Benchmark;

/// <summary>One part of a round of the race route: a walk or a build, alone or raced.</summary>
/// <param name="Elapsed">How long the part took, from its start to its end.</param>
/// <param name="Heap">What the collector did between the part's start and its end, across the whole process.</param>
/// <param name="Items">What the part counted: entries or candidates for the walk, records for the build.</param>
/// <param name="Complete">Whether the part answered for everything, as <see cref="RunTally.Complete"/> says.</param>
internal readonly record struct RacePart(TimeSpan Elapsed, HeapCounters Heap, long Items, bool Complete);

/// <summary>What one round of the race route measured, part by part.</summary>
/// <param name="Racer">The route that walked: <see cref="Route.Walk"/> or <see cref="Route.Sources"/>.</param>
/// <param name="WalkAlone">The walk with nothing beside it.</param>
/// <param name="BuildAlone">The index build with nothing beside it.</param>
/// <param name="WalkRaced">The walk, started at the same moment as the build.</param>
/// <param name="BuildRaced">The build, started at the same moment as the walk.</param>
/// <param name="Reversed">
/// Whether the round ran the raced pair first and the walk alone last, as every other round does, so
/// a cache one part leaves warm favours neither the walk alone nor the walk raced.
/// </param>
internal sealed record RaceTimes(
    Route Racer,
    RacePart WalkAlone,
    RacePart BuildAlone,
    RacePart WalkRaced,
    RacePart BuildRaced,
    bool Reversed)
{
    /// <summary>How many times as long the walk took raced as alone.</summary>
    public double Slowdown => WalkRaced.Elapsed.TotalSeconds / Math.Max(WalkAlone.Elapsed.TotalSeconds, 1e-9);

    /// <summary>How many times as long the build took raced as alone.</summary>
    public double BuildSlowdown => BuildRaced.Elapsed.TotalSeconds / Math.Max(BuildAlone.Elapsed.TotalSeconds, 1e-9);

    public bool Complete => WalkAlone.Complete && BuildAlone.Complete && WalkRaced.Complete && BuildRaced.Complete;
}
