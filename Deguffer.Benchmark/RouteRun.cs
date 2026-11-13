namespace Deguffer.Benchmark;

/// <summary>
/// One route as the run loop drives it: what is timed, and what comes before and after each timed
/// run without being timed.
/// </summary>
/// <param name="Run">The work that is timed.</param>
/// <param name="Before">
/// Made ready before each run, outside its time: the tree a removal route removes is built here.
/// Nothing for a route that only reads.
/// </param>
/// <param name="After">
/// Checked after each run, outside its time: a removal route proves here that it took nothing beside
/// its target (§5.6). Nothing for a route that only reads. Never cancelled, because a check left
/// half made would let a run that took too much pass.
/// </param>
internal sealed record RouteRun(
    Func<CancellationToken, ValueTask<RunTally>> Run,
    Func<CancellationToken, ValueTask>? Before = null,
    Func<ValueTask>? After = null)
{
    /// <summary>A route whose work, and what comes before and after it, runs on the thread that times it.</summary>
    public static RouteRun Blocking(Func<CancellationToken, RunTally> run, Action<CancellationToken>? before = null, Action? after = null) => new(
        ct => ValueTask.FromResult(run(ct)),
        before is null
            ? null
            : ct =>
            {
                before(ct);
                return ValueTask.CompletedTask;
            },
        after is null
            ? null
            : () =>
            {
                after();
                return ValueTask.CompletedTask;
            });
}
