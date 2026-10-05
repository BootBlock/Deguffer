namespace Deguffer.Core.Scanning.Mft;

/// <summary>What became of one record a pass read, as far as the pass has to know.</summary>
internal enum MftHandOn
{
    /// <summary>Handed on, held back for the second pass, or free and passed over. The pass goes on.</summary>
    Done,

    /// <summary>
    /// Held back by a table that wants more extension records than it holds. The pass stops at it,
    /// and the table was not read whole.
    /// </summary>
    WantsTooMuch,

    /// <summary>The handler asked to stop.</summary>
    Abandoned,
}

/// <summary>
/// What the first pass does with each record it reads: passes over one the bitmap marks free, holds
/// back one whose attributes continue in extension records, and hands on the rest.
///
/// <para>Called from every parse thread at once, each with a different record.</para>
/// </summary>
/// <param name="count">How many records the table holds, which bounds what it can want.</param>
internal sealed class MftRecordHandOff(int count, MftBitmap? bitmap, MftRecordHandler onRecord)
{
    private readonly List<MftDeferredRecord> _deferred = [];

    private long _wanted;

    /// <summary>How many extension records the records held back want between them.</summary>
    public long Wanted => Interlocked.Read(ref _wanted);

    /// <summary>The records held back, in record order. Only once every parse has finished.</summary>
    public IReadOnlyList<MftDeferredRecord> Deferred()
    {
        _deferred.Sort(static (a, b) => a.Self.Record.CompareTo(b.Self.Record));
        return _deferred;
    }

    /// <param name="record">The record's bytes, exactly, which the parse fixes up in place.</param>
    public MftHandOn Take(long number, Span<byte> record)
    {
        if (bitmap is not null && !bitmap.IsInUse(number))
        {
            return MftHandOn.Done;
        }

        var outcome = MftRecordParser.Parse(record, number, out var parsed, out var continued);

        if (continued is not null)
        {
            // A table wanting more extension records than it holds was not written by NTFS, and
            // holding every want of a hostile one would grow without bound.
            if (Interlocked.Add(ref _wanted, continued.Segments.Count) > count)
            {
                return MftHandOn.WantsTooMuch;
            }

            lock (_deferred)
            {
                _deferred.Add(continued);
            }

            return MftHandOn.Done;
        }

        return onRecord(number, outcome, in parsed) ? MftHandOn.Done : MftHandOn.Abandoned;
    }
}
