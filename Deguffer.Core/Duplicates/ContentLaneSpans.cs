namespace Deguffer.Core.Duplicates;

/// <summary>
/// Records each read of one content comparison by its lane and part, from every reader's thread at
/// once, for <see cref="DuplicateSearchTimings"/>. Without a lock: each count is added to on its own,
/// and each span only widens.
/// </summary>
internal sealed class ContentLaneSpans
{
    private const int PartsPerLane = (int)ContentPart.Whole + 1;

    private readonly IReadOnlyList<int> _readers;
    private readonly TimeProvider _clock;

    /// <summary>Each lane's spans, a lane's parts side by side.</summary>
    private readonly ReadSpan[] _spans;

    public ContentLaneSpans(IReadOnlyList<int> readers, TimeProvider clock)
    {
        _readers = readers;
        _clock = clock;
        _spans = new ReadSpan[readers.Count * PartsPerLane];

        for (var i = 0; i < _spans.Length; i++)
        {
            _spans[i] = new ReadSpan();
        }
    }

    /// <summary>The moment a read starts, to hand to <see cref="Read"/> once it has ended.</summary>
    public long Now() => _clock.GetTimestamp();

    /// <summary>Record a read of <paramref name="part"/> in <paramref name="lane"/> that started at <paramref name="started"/> and has just ended.</summary>
    /// <param name="bytes">What the read covers of its file (<see cref="ContentReader.BytesOf"/>), counted only where it read them from the disk.</param>
    public void Read(int lane, ContentPart part, ContentReading reading, long bytes, long started) =>
        _spans[(lane * PartsPerLane) + (int)part].Add(reading, bytes, started, _clock.GetTimestamp());

    /// <summary>Every lane's reads of <paramref name="part"/> together.</summary>
    public ContentReadTally Tally(ContentPart part) => Tally([.. Enumerable.Range(0, _readers.Count).Select(lane => Span(lane, part))]);

    public IReadOnlyList<ContentLaneTimes> Lanes() =>
        [.. Enumerable.Range(0, _readers.Count).Select(lane =>
        {
            var ends = Span(lane, ContentPart.Ends);
            var whole = Span(lane, ContentPart.Whole);

            return new ContentLaneTimes(_readers[lane], Tally([ends]), Tally([whole]), Tally([ends, whole]).Elapsed);
        })];

    private ReadSpan Span(int lane, ContentPart part) => _spans[(lane * PartsPerLane) + (int)part];

    /// <summary>What <paramref name="spans"/> read between them, from the first read of any to the last.</summary>
    private ContentReadTally Tally(IReadOnlyList<ReadSpan> spans)
    {
        var files = spans.Sum(span => span.Files);

        return new ContentReadTally(
            files,
            spans.Sum(span => span.Remembered),
            spans.Sum(span => span.LeftOut),
            spans.Sum(span => span.Bytes),
            files == 0 ? TimeSpan.Zero : _clock.GetElapsedTime(spans.Min(span => span.First), spans.Max(span => span.Last)));
    }

    /// <summary>The reads of one part in one lane, added to from any reader's thread.</summary>
    private sealed class ReadSpan
    {
        private int _files;
        private int _remembered;
        private int _leftOut;
        private long _bytes;
        private long _first = long.MaxValue;
        private long _last = long.MinValue;

        public int Files => Volatile.Read(ref _files);

        public int Remembered => Volatile.Read(ref _remembered);

        public int LeftOut => Volatile.Read(ref _leftOut);

        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>When the first read started, or the largest timestamp where none was made.</summary>
        public long First => Interlocked.Read(ref _first);

        /// <summary>When the last read ended, or the smallest timestamp where none was made.</summary>
        public long Last => Interlocked.Read(ref _last);

        public void Add(ContentReading reading, long bytes, long started, long ended)
        {
            Interlocked.Increment(ref _files);

            if (reading.Result is not ContentReadResult.Read)
            {
                Interlocked.Increment(ref _leftOut);
            }
            else if (reading.Remembered)
            {
                Interlocked.Increment(ref _remembered);
            }
            else
            {
                Interlocked.Add(ref _bytes, bytes);
            }

            Widen(ref _first, started, static (held, offered) => offered < held);
            Widen(ref _last, ended, static (held, offered) => offered > held);
        }

        /// <summary>Replace <paramref name="field"/> with <paramref name="offered"/> where it is further out, whatever other readers write meanwhile.</summary>
        private static void Widen(ref long field, long offered, Func<long, long, bool> further)
        {
            var held = Interlocked.Read(ref field);

            while (further(held, offered))
            {
                var seen = Interlocked.CompareExchange(ref field, offered, held);

                if (seen == held)
                {
                    return;
                }

                held = seen;
            }
        }
    }
}
