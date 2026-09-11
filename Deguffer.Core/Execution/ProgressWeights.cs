namespace Deguffer.Core.Execution;

/// <summary>
/// What share of a progress bar each part of a run gets.
///
/// <para>Weighted by what the part expects to free, never by how many parts there are. A 40 GB
/// cache and a 5 MB one are one part each, so an equal split would park the bar half way through
/// for the whole of the stretch that takes any time. Bytes are a proxy for that time rather than a
/// measure of it — per-file overhead dominates a delete, not volume — but it is the figure planning
/// actually produced, and it is the one already on the screen beside each row.</para>
///
/// <para>One rule for both levels of the run, plans and the steps inside them, because a bar that
/// weighted providers by size and their steps by count would be honest at one scale and wrong at
/// the other.</para>
/// </summary>
internal static class ProgressWeights
{
    /// <summary>
    /// A weight per estimate, in the order given.
    ///
    /// Where nothing carries an estimate they come back one apiece. That is the run made entirely
    /// of command steps whose own tool reports no figure, and the count is then the best answer
    /// available. An empty input is the one case whose weights still sum to zero, and it divides by
    /// nothing: a caller iterating the parts never reaches the division.
    ///
    /// <para><b>A part with no estimate beside parts that have one gets a share of nothing</b>, and
    /// the bar stands still for as long as it runs. The fallback above is all-or-nothing on purpose:
    /// the alternative is inventing a figure for the unmeasured part, and a made-up weight moves the
    /// bar by an amount that means nothing. Nothing invents one here, so the caller's job is to hand
    /// over parts that were all measured the same way.</para>
    ///
    /// <para>Deguffer's own callers nearly do. A step is untickable unless it removes something, and a
    /// plan is narrowed to its ticked steps before it executes — see <c>StepViewModel.CanBeSelected</c>
    /// and <see cref="CleanupPlan.NarrowedTo"/>. The one ticked step that carries no byte estimate is a
    /// leftover of empty folders (<see cref="DeleteStep.IsLeftover"/>), and a share of nothing is right
    /// for it: removing an empty folder takes no time the bar could show. A run of nothing else falls
    /// back to the count above. The one plan a caller may hand the planner with no estimate by design is
    /// one run only to be verified, and <see cref="CleanupPlanner"/> weighs those apart rather than
    /// handing them here.</para>
    /// </summary>
    public static IReadOnlyList<double> For(IEnumerable<long> estimates)
    {
        var weights = estimates.Select(e => (double)Math.Max(0, e)).ToList();

        return weights.Sum() > 0 ? weights : [.. weights.Select(_ => 1.0)];
    }
}
