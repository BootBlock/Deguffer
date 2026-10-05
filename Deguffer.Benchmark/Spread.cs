namespace Deguffer.Benchmark;

/// <summary>
/// The median of a set of measurements and the range they covered.
///
/// <para>The median rather than the mean, because one run delayed by something else on the machine
/// moves a mean and does not move a median. The range is kept beside it so a reader can see when
/// the runs disagreed too much for the median to mean anything.</para>
/// </summary>
internal readonly record struct Spread(double Median, double Minimum, double Maximum)
{
    public static Spread Of(IReadOnlyList<double> values)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        var median = sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;

        return new Spread(median, sorted[0], sorted[^1]);
    }
}
