namespace Deguffer.Core.Execution;

/// <summary>
/// Maps a nested fraction onto a span of an outer one, so a part that reports 0 to 1 about itself
/// contributes 0 to 1 about the whole run.
///
/// <para>Deliberately not a <see cref="Progress{T}"/>. That type posts every report to the context
/// captured when it was constructed, and here that context is the thread pool — the shell runs a
/// clean on a worker — so two reports can be delivered in the order the pool feels like. The value
/// that arrives is then not the latest one, and a bar that goes backwards is the single thing a
/// progress bar must never do. Reporting straight through keeps the order the caller produced.</para>
/// </summary>
/// <param name="outer">Where the scaled value goes.</param>
/// <param name="offset">Where this part starts, as a fraction of the whole.</param>
/// <param name="scale">How much of the whole this part accounts for.</param>
internal sealed class ScaledProgress(IProgress<double> outer, double offset, double scale)
    : IProgress<double>
{
    /// <summary>
    /// A scaled view of <paramref name="outer"/>, or null where there is nothing to report to —
    /// so a caller can pass the result straight on without testing for null itself.
    /// </summary>
    public static IProgress<double>? Within(IProgress<double>? outer, double offset, double scale) =>
        outer is null ? null : new ScaledProgress(outer, offset, scale);

    public void Report(double value) => outer.Report(offset + (value * scale));
}
