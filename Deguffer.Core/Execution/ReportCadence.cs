namespace Deguffer.Core.Execution;

/// <summary>
/// When a step's work, done on several threads at once, next says how far it has got: at most once per
/// <see cref="Interval"/>, and only to the one thread that claims the report.
///
/// <para><b>By time, not by count.</b> Each report is a post to the UI thread, and a count would set
/// their rate by how fast the work goes: hundreds a second on a fast volume, for a bar that moves by
/// half a percent at the least. The first report is due one interval in, so work over sooner than that
/// posts only its end, which the caller reports whatever this says.</para>
/// </summary>
internal sealed class ReportCadence
{
    /// <summary>How often the work says how far it has got, which is often enough to watch and rare enough not to flood the UI thread.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly TimeProvider _time;

    // Interval in the clock's own timestamp units, so each claim compares two longs.
    private readonly long _interval;

    // The timestamp from which the next report may be made.
    private long _due;

    /// <param name="time">What times the reports: the seam a test reports through.</param>
    public ReportCadence(TimeProvider time)
    {
        _time = time;
        _interval = (long)(Interval.TotalSeconds * time.TimestampFrequency);
        _due = time.GetTimestamp() + _interval;
    }

    /// <summary>
    /// True where a report is due and this caller is the one to make it. Of the threads that find a
    /// report due together, only the one that moves the next due time on is told to report.
    /// </summary>
    public bool Claim()
    {
        var due = Interlocked.Read(ref _due);
        var now = _time.GetTimestamp();

        return now >= due && Interlocked.CompareExchange(ref _due, now + _interval, due) == due;
    }
}
