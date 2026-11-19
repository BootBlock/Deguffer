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
/// <para><b>Reported by time, not by count</b> (<see cref="ReportCadence"/>). Every entry is counted,
/// so the fraction is exact, but a report is made only when one is due.</para>
/// </summary>
internal sealed class RemovalProgress
{
    private readonly ForwardProgress _forward;
    private readonly ReportCadence _cadence;
    private readonly long _planned;

    private long _found;
    private long _done;
    private bool _counted;

    /// <param name="progress">Where the fraction goes, or null where nobody is watching.</param>
    /// <param name="planned">How many entries the plan measured, or zero where it measured none.</param>
    /// <param name="time">What times the reports: the seam a test reports through.</param>
    public RemovalProgress(IProgress<double>? progress, long planned, TimeProvider time)
    {
        _forward = new ForwardProgress(progress);
        _cadence = new ReportCadence(time);
        _planned = planned;
    }

    /// <summary>Count <paramref name="entries"/> more as found by the walk and handed to the removal.</summary>
    public void Found(long entries) => Interlocked.Add(ref _found, entries);

    /// <summary>The walk is over, so what it found is the whole of the removal.</summary>
    public void Counted() => Volatile.Write(ref _counted, true);

    /// <summary>Count one entry done, whatever became of it, and report if a report is due.</summary>
    public void Done()
    {
        Interlocked.Increment(ref _done);

        if (_cadence.Claim())
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
