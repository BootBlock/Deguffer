namespace Deguffer.Core.Duplicates;

/// <summary>
/// How far a content search has got, in bytes: those read, against those read and the most still to
/// read (<see cref="DuplicateSearchProgress.Total"/>).
///
/// <para><b>One measure, though the stages overlap.</b> The first and last blocks of one group are
/// read beside the whole of another, so neither stage's count says how far the search is. Bytes do,
/// because a whole read costs by its length and a read of the ends by two blocks.</para>
///
/// <para><b>The share read only grows.</b> The most still to read starts as every file's ends and,
/// where its ends are not the whole file, its full length. A read moves its bytes from that to the
/// bytes read, and a file that stops matching takes its full read off it, so the total falls and
/// the share rises. Each change and the report it makes are under one lock, so no report reaches the
/// page ahead of one made before it.</para>
/// </summary>
internal sealed class ContentProgress
{
    /// <summary>How often the search says how far it has got, which is often enough to watch and rare enough not to flood the page.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    private readonly IProgress<DuplicateSearchProgress>? _progress;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private long _done;
    private long _left;
    private long _reported;

    /// <param name="left">The most the search will read: every file's ends, and the rest of each larger one.</param>
    /// <param name="clock">What times the reports: the seam a test reports every change through.</param>
    public ContentProgress(long left, IProgress<DuplicateSearchProgress>? progress, TimeProvider clock)
    {
        _left = left;
        _progress = progress;
        _clock = clock;
        _reported = clock.GetTimestamp();

        _progress?.Report(new DuplicateSearchProgress(DuplicateSearchStage.ComparingContent, 0, left));
    }

    /// <summary>The most a search reads of a file of <paramref name="length"/> bytes: its ends, and then all of it unless they were.</summary>
    public static long MostToRead(long length) =>
        ContentReader.BytesOf(ContentPart.Ends, length) + (ContentReader.IsWhole(ContentPart.Ends, length) ? 0 : length);

    /// <summary>Count a read of <paramref name="bytes"/>, whether or not it succeeded, as no longer to be read.</summary>
    public void Read(long bytes) => Change(bytes, bytes);

    /// <summary>Take <paramref name="bytes"/> the search will no longer read off what is left.</summary>
    public void Dropped(long bytes) => Change(0, bytes);

    /// <summary>Report that nothing is left, whenever the last report was.</summary>
    public void Finished()
    {
        lock (_gate)
        {
            _progress?.Report(new DuplicateSearchProgress(DuplicateSearchStage.ComparingContent, _done, _done + _left));
        }
    }

    private void Change(long read, long gone)
    {
        lock (_gate)
        {
            _done += read;
            _left -= gone;

            var now = _clock.GetTimestamp();
            if (_clock.GetElapsedTime(_reported, now) >= ReportEvery)
            {
                _reported = now;
                _progress?.Report(new DuplicateSearchProgress(DuplicateSearchStage.ComparingContent, _done, _done + _left));
            }
        }
    }
}
