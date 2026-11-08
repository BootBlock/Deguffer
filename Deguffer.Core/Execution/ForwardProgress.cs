namespace Deguffer.Core.Execution;

/// <summary>
/// A fraction reported from several threads, passed on in the order it can only ever grow in.
///
/// <para><b>Never moves backwards.</b> Reports pass straight through to the bar
/// (<see cref="ScaledProgress"/>), so two threads that each reached a count could otherwise deliver the
/// later one first, and a bar that goes backwards is the one thing a progress bar must never do. A
/// report that would fall below the last one is dropped, except the whole, which is always passed on.
/// </para>
/// </summary>
/// <param name="progress">Where the fraction goes, or null where nobody is watching.</param>
internal sealed class ForwardProgress(IProgress<double>? progress)
{
    private readonly Lock _reporting = new();
    private double _reported;

    public void Report(double fraction)
    {
        if (progress is null)
        {
            return;
        }

        lock (_reporting)
        {
            if (fraction <= _reported && fraction < 1.0)
            {
                return;
            }

            _reported = fraction;
            progress.Report(fraction);
        }
    }
}
