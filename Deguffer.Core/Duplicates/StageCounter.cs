namespace Deguffer.Core.Duplicates;

/// <summary>
/// Counts the files one stage of a duplicate search has finished with, from any of its readers'
/// threads, and says how far the stage has got: once as it starts, at most every
/// <see cref="ReportEveryMilliseconds"/> while it runs, and once more with its last file.
/// </summary>
internal sealed class StageCounter
{
    /// <summary>How often a stage says how far it has got, which is often enough to watch and rare enough not to flood the page.</summary>
    private const long ReportEveryMilliseconds = 100;

    private readonly DuplicateSearchStage _stage;
    private readonly int _total;
    private readonly IProgress<DuplicateSearchProgress>? _progress;

    private int _done;
    private long _reported = Environment.TickCount64;

    /// <param name="total">How many files the stage finishes with.</param>
    public StageCounter(DuplicateSearchStage stage, int total, IProgress<DuplicateSearchProgress>? progress)
    {
        _stage = stage;
        _total = total;
        _progress = progress;

        progress?.Report(new DuplicateSearchProgress(stage, 0, total));
    }

    /// <summary>Count one more file finished with, and say so where it is time to or it was the last.</summary>
    public void Finished()
    {
        var finished = Interlocked.Increment(ref _done);
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _reported);

        if (finished == _total || (now - last >= ReportEveryMilliseconds && Interlocked.CompareExchange(ref _reported, now, last) == last))
        {
            _progress?.Report(new DuplicateSearchProgress(_stage, finished, _total));
        }
    }
}
