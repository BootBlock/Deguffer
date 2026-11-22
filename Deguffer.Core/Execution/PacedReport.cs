namespace Deguffer.Core.Execution;

/// <summary>
/// When something that changes on several threads next reports its latest state: at once after a quiet
/// spell, and otherwise once <see cref="ReportCadence.Interval"/> has passed since the last report.
///
/// <para><b>The last change is never left unsaid.</b> <see cref="ReportCadence"/> is asked only when
/// work moves, so a change made inside an interval and followed by a long wait, a provider waiting on its
/// tool, would go unreported until the wait ended. Here a change made too soon arms a timer for the end
/// of the interval instead, and the report it makes says whatever is latest by then. A burst of changes
/// costs one report at its start and one at the end of each interval it spans.</para>
///
/// <para><b>What is reported is read when the report is made</b>, not when the change is, so nothing
/// about a change that is never shown is worked out. Two reports can run at once, the timer's and one
/// made at once after a quiet spell, so the report must be safe on any thread.</para>
/// </summary>
internal sealed class PacedReport : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Action _report;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();

    // Everything below is read and written under _gate.
    private long? _lastReported;
    private bool _timed;
    private bool _disposed;

    /// <param name="time">What times the reports: the seam a test reports through.</param>
    /// <param name="report">Reads the latest state and reports it, on whichever thread a report is made.</param>
    public PacedReport(TimeProvider time, Action report)
    {
        _time = time;
        _report = report;
        _timer = time.CreateTimer(_ => Due(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Say the state has changed, from any thread, and report it now if a report is due.</summary>
    public void Changed()
    {
        lock (_gate)
        {
            // A report the timer will make says this change too.
            if (_timed || _disposed)
            {
                return;
            }

            var wait = _lastReported is { } last ? ReportCadence.Interval - _time.GetElapsedTime(last) : TimeSpan.Zero;

            if (wait > TimeSpan.Zero)
            {
                _timed = true;
                _timer.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastReported = _time.GetTimestamp();
        }

        _report();
    }

    /// <summary>Make no report from now on, a timed one included.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Due()
    {
        lock (_gate)
        {
            if (!_timed || _disposed)
            {
                return;
            }

            _timed = false;
            _lastReported = _time.GetTimestamp();
        }

        _report();
    }
}
