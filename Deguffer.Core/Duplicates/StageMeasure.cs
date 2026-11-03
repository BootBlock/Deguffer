namespace Deguffer.Core.Duplicates;

/// <summary>
/// How far one stage of a duplicate search has got, in the stage's own unit: what is done, against
/// what is done and the most still to do (<see cref="DuplicateSearchProgress.Total"/>). Identifying
/// counts files, each asked about once. Comparing content counts bytes.
///
/// <para><b>Comparing content: one measure, though its stages overlap.</b> The first and last blocks
/// of one group are read beside the whole of another, so neither stage's count says how far the
/// search is. Bytes do, because a whole read costs by its length and a read of the ends by two
/// blocks. The most still to read starts as every file's ends and, where its ends are not the whole
/// file, its full length (<see cref="ContentReader.MostToRead"/>).</para>
///
/// <para><b>The share done only grows.</b> Work done moves from what is left to what is done, and
/// work the stage will no longer do is taken off what is left, so the total only falls and the share
/// only rises. Each change and the report it makes are under one lock, so with several readers no
/// report reaches the page ahead of one made before it, and the last says the stage is done.</para>
/// </summary>
internal sealed class StageMeasure
{
    /// <summary>How often the search says how far it has got, which is often enough to watch and rare enough not to flood the page.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    private readonly DuplicateSearchStage _stage;
    private readonly IProgress<DuplicateSearchProgress>? _progress;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private long _done;
    private long _left;
    private long _reported;

    /// <param name="left">The most the stage will do.</param>
    /// <param name="clock">What times the reports: the seam a test reports every change through.</param>
    public StageMeasure(DuplicateSearchStage stage, long left, IProgress<DuplicateSearchProgress>? progress, TimeProvider clock)
    {
        _stage = stage;
        _left = left;
        _progress = progress;
        _clock = clock;
        _reported = clock.GetTimestamp();

        _progress?.Report(new DuplicateSearchProgress(stage, 0, left));
    }

    /// <summary>Count <paramref name="amount"/> as done, whether or not it succeeded, and so as no longer to do.</summary>
    public void Done(long amount) => Change(amount, amount);

    /// <summary>Take <paramref name="amount"/> the stage will no longer do off what is left.</summary>
    public void Dropped(long amount) => Change(0, amount);

    /// <summary>Report that nothing is left, whenever the last report was.</summary>
    public void Finished()
    {
        lock (_gate)
        {
            _progress?.Report(new DuplicateSearchProgress(_stage, _done, _done + _left));
        }
    }

    private void Change(long done, long gone)
    {
        lock (_gate)
        {
            _done += done;
            _left -= gone;

            var now = _clock.GetTimestamp();
            if (_clock.GetElapsedTime(_reported, now) >= ReportEvery)
            {
                _reported = now;
                _progress?.Report(new DuplicateSearchProgress(_stage, _done, _done + _left));
            }
        }
    }
}
