namespace Deguffer.Core.Execution;

/// <summary>
/// One plan's progress while several of its steps run at once: the weight of every step that has
/// finished, and each running step's own fraction of its weight, as one fraction of the plan.
///
/// <para><b>Reported under one lock.</b> Each step reports from its own thread, and a sum worked out
/// on one thread and reported after a larger sum from another would move the bar backwards, which
/// <see cref="ScaledProgress"/> exists to prevent. A step's fraction is also never lowered, so the sum
/// can only grow.</para>
/// </summary>
/// <param name="weights">Each step's share of the plan, as <see cref="ProgressWeights.For"/> gives it.</param>
internal sealed class StepsProgress(IProgress<double> outer, IReadOnlyList<double> weights)
{
    private readonly Lock _gate = new();
    private readonly double[] _done = new double[weights.Count];
    private readonly double _total = weights.Sum();
    private double _sum;

    /// <summary>
    /// Where the step at <paramref name="step"/> reports its own 0 to 1, or null where there is nothing
    /// to report to.
    /// </summary>
    public static IProgress<double>? Of(StepsProgress? plan, int step) => plan is null ? null : new Step(plan, step);

    /// <summary>The step at <paramref name="step"/> has ended, which is all of its weight whatever it last reported.</summary>
    public void Finished(int step) => Report(step, 1.0);

    private void Report(int step, double fraction)
    {
        lock (_gate)
        {
            var now = Math.Max(_done[step], Math.Clamp(fraction, 0.0, 1.0));

            // Kept as a running sum, because a removal reports many times and a row can be hundreds of
            // steps long.
            _sum += (now - _done[step]) * weights[step];
            _done[step] = now;

            outer.Report(_sum / _total);
        }
    }

    private sealed class Step(StepsProgress plan, int step) : IProgress<double>
    {
        public void Report(double value) => plan.Report(step, value);
    }
}
