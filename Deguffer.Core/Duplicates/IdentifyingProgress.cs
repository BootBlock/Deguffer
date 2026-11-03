namespace Deguffer.Core.Duplicates;

/// <summary>
/// How far identifying the files that may match has got, in files: those Windows has been asked
/// about, against all the pass asks about (<see cref="DuplicateSearchStage.Identifying"/>). Counted
/// from any reader's thread, and said once as the pass starts, at most every
/// <see cref="ReportEveryMilliseconds"/> while it runs, and once more with its last file.
/// </summary>
internal sealed class IdentifyingProgress
{
    /// <summary>How often the search says how far it has got, which is often enough to watch and rare enough not to flood the page.</summary>
    private const long ReportEveryMilliseconds = 100;

    private readonly int _total;
    private readonly IProgress<DuplicateSearchProgress>? _progress;

    private int _done;
    private long _reported = Environment.TickCount64;

    /// <param name="total">How many files the pass asks about.</param>
    public IdentifyingProgress(int total, IProgress<DuplicateSearchProgress>? progress)
    {
        _total = total;
        _progress = progress;

        progress?.Report(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, 0, total));
    }

    /// <summary>Count one more file asked about, and say so where it is time to or it was the last.</summary>
    public void Identified()
    {
        var done = Interlocked.Increment(ref _done);
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _reported);

        if (done == _total || (now - last >= ReportEveryMilliseconds && Interlocked.CompareExchange(ref _reported, now, last) == last))
        {
            _progress?.Report(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, done, _total));
        }
    }
}
