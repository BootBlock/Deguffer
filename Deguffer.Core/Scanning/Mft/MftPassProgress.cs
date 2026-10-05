namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How far a pass over the table has got, counted in records dealt with: read and handed on, or known
/// from the bitmap to be free and never read.
///
/// <para><b>A report runs under a lock</b>, so it never runs beside another, and the count it gives
/// never goes back, though the parse threads count at once. None is made once the pass is
/// stopping.</para>
/// </summary>
internal sealed class MftPassProgress(Action<long>? onProgress, Func<bool> stopping)
{
    /// <summary>How many records apart reports are, at least.</summary>
    internal const int Interval = 65536;

    private readonly Lock _reporting = new();

    private long _done;
    private long _nextReport = Interval;

    /// <summary>
    /// Report that nothing is dealt with yet, so a progress bar shows the pass has begun however long
    /// the first interval takes.
    /// </summary>
    public void Begin() => onProgress?.Invoke(0);

    /// <summary>Count <paramref name="records"/> as dealt with, and report if a report is due.</summary>
    public void Advance(long records)
    {
        if (onProgress is null || records == 0)
        {
            return;
        }

        var done = Interlocked.Add(ref _done, records);
        if (done < Volatile.Read(ref _nextReport))
        {
            return;
        }

        lock (_reporting)
        {
            // Asked again under the lock: the thread that held it may have just reported.
            done = Interlocked.Read(ref _done);
            if (stopping() || done < _nextReport)
            {
                return;
            }

            Volatile.Write(ref _nextReport, done - (done % Interval) + Interval);
            onProgress(done);
        }
    }
}
