namespace Deguffer.Core.Execution;

/// <summary>
/// How far a removal that deletes while it is still listing has got, as a fraction of the entries it
/// will take.
///
/// <para><b>Measured against the plan until the walk has counted the tree.</b> While the walk runs,
/// nobody knows how many entries the tree holds, so the fraction is of what the plan measured
/// (<see cref="Scanning.ScanSize.Entries"/>), or of what the walk has found so far where that is
/// already more. Once the walk is over, the count it made takes over.</para>
///
/// <para><b>Never moves backwards</b> (<see cref="ForwardProgress"/>). A plan that measured too little
/// would otherwise pull the bar back each time the walk found more than it promised.</para>
///
/// <para><b>Reported by time, not by count.</b> Every entry is counted, so the fraction is exact, but
/// a report is made at most once per <see cref="ReportEvery"/>. Each one is a post to the UI thread,
/// and a count would set their rate by how fast the volume deletes: hundreds a second on a fast one,
/// for a bar that moves by half a percent at the least.</para>
/// </summary>
internal sealed class RemovalProgress
{
    /// <summary>How often a removal says how far it has got, which is often enough to watch and rare enough not to flood the UI thread.</summary>
    internal static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    private readonly ForwardProgress _forward;
    private readonly long _planned;
    private readonly TimeProvider _time;

    // ReportEvery in the clock's own timestamp units, so the gate compares two longs per entry.
    private readonly long _interval;

    private long _found;
    private long _done;
    private bool _counted;

    // The timestamp from which the next report may be made. The first is one interval in, so a removal
    // over sooner than that posts only its end.
    private long _due;

    /// <param name="progress">Where the fraction goes, or null where nobody is watching.</param>
    /// <param name="planned">How many entries the plan measured, or zero where it measured none.</param>
    /// <param name="time">What times the reports: the seam a test reports through.</param>
    public RemovalProgress(IProgress<double>? progress, long planned, TimeProvider time)
    {
        _forward = new ForwardProgress(progress);
        _planned = planned;
        _time = time;
        _interval = (long)(ReportEvery.TotalSeconds * time.TimestampFrequency);
        _due = time.GetTimestamp() + _interval;
    }

    /// <summary>Count <paramref name="entries"/> more as found by the walk and handed to the removal.</summary>
    public void Found(long entries) => Interlocked.Add(ref _found, entries);

    /// <summary>The walk is over, so what it found is the whole of the removal.</summary>
    public void Counted() => Volatile.Write(ref _counted, true);

    /// <summary>Count one entry done, whatever became of it, and report if the last report is old enough.</summary>
    public void Done()
    {
        Interlocked.Increment(ref _done);

        var due = Interlocked.Read(ref _due);
        var now = _time.GetTimestamp();

        // Of the threads that find a report due, only the one that moves the next due time on reports.
        if (now >= due && Interlocked.CompareExchange(ref _due, now + _interval, due) == due)
        {
            Report();
        }
    }

    /// <summary>Report the removal finished.</summary>
    public void Finished() => _forward.Report(1.0);

    private void Report()
    {
        var found = Interlocked.Read(ref _found);
        var whole = Volatile.Read(ref _counted) ? found : Math.Max(_planned, found);

        _forward.Report(whole == 0 ? 1.0 : Math.Min(1.0, (double)Interlocked.Read(ref _done) / whole));
    }
}
