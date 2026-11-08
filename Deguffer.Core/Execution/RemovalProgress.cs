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
/// </summary>
/// <param name="progress">Where the fraction goes, or null where nobody is watching.</param>
/// <param name="planned">How many entries the plan measured, or zero where it measured none.</param>
internal sealed class RemovalProgress(IProgress<double>? progress, long planned)
{
    /// <summary>
    /// How many entries pass between reports. The UI cannot use thousands of reports a second, and
    /// marshalling them would cost more than the deletions they describe.
    /// </summary>
    private const int ReportEvery = 256;

    private readonly ForwardProgress _forward = new(progress);

    private long _found;
    private long _done;
    private bool _counted;

    /// <summary>Count <paramref name="entries"/> more as found by the walk and handed to the removal.</summary>
    public void Found(long entries) => Interlocked.Add(ref _found, entries);

    /// <summary>The walk is over, so what it found is the whole of the removal.</summary>
    public void Counted() => Volatile.Write(ref _counted, true);

    /// <summary>Count one entry done, whatever became of it, and report every so often.</summary>
    public void Done()
    {
        if (Interlocked.Increment(ref _done) % ReportEvery == 0)
        {
            Report();
        }
    }

    /// <summary>Report the removal finished.</summary>
    public void Finished() => _forward.Report(1.0);

    private void Report()
    {
        var found = Interlocked.Read(ref _found);
        var whole = Volatile.Read(ref _counted) ? found : Math.Max(planned, found);

        _forward.Report(whole == 0 ? 1.0 : Math.Min(1.0, (double)Interlocked.Read(ref _done) / whole));
    }
}
