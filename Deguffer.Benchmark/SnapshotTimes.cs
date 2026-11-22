namespace Deguffer.Benchmark;

/// <summary>
/// What the snapshots an Explore run assembled cost it: how many were started, and the time spent
/// assembling them, summed.
///
/// <para>Assembled beside the read or walk rather than as a stage of it, so the time is not part of
/// the run's elapsed time in the way a stage's is. It is processor time taken from the run, and the
/// difference between a run with snapshots and one without is what it cost the run's elapsed time.
/// </para>
/// </summary>
/// <param name="Count">
/// How many snapshots were started. The last can be stopped part of the way through when the run
/// ends, and is counted, because the time it took before it stopped was spent.
/// </param>
/// <param name="Assembling">The time every snapshot took to assemble, from its start to its end, summed.</param>
internal readonly record struct SnapshotTimes(int Count, TimeSpan Assembling);
