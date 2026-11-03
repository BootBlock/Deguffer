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
/// its target (§5.6). Nothing for a route that only reads.
/// </param>
internal sealed record RouteRun(
    Func<CancellationToken, RunTally> Run,
    Action<CancellationToken>? Before = null,
    Action? After = null);
