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
/// back one whose attributes continue in extension records, keeps what an extension record holds for
/// the second pass, and hands on the rest.
///
/// <para>Called from every parse thread at once, each with a different record.</para>
/// </summary>
/// <param name="count">How many records the table holds, which bounds what it can want.</param>
internal sealed class MftRecordHandOff(int count, MftBitmap? bitmap, MftRecordHandler onRecord)
{
    private readonly List<MftDeferredRecord> _deferred = [];
    private readonly List<MftExtensionRecord> _extensions = [];

    private long _wanted;

    /// <summary>How many extension records the records held back want between them.</summary>
    public long Wanted => Interlocked.Read(ref _wanted);

    /// <summary>The records held back, in record order. Only once every parse has finished.</summary>
    public IReadOnlyList<MftDeferredRecord> Deferred()
    {
        _deferred.Sort(static (a, b) => a.Self.Record.CompareTo(b.Self.Record));
        return _deferred;
    }

    /// <summary>
    /// The extension records the pass read that hold something the scan needs, in record order. Only
    /// once every parse has finished.
    /// </summary>
    public IReadOnlyList<MftExtensionRecord> Extensions()
    {
        _extensions.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        return _extensions;
    }

    /// <param name="record">The record's bytes, exactly, which the parse fixes up in place.</param>
    public MftHandOn Take(long number, Span<byte> record)
    {
        if (bitmap is not null && !bitmap.IsInUse(number))
        {
            return MftHandOn.Done;
        }

        var outcome = MftRecordParser.Parse(record, number, out var parsed, out var name, out var continued, out var extension);

        // Most extension records hold only what the scan never asks for: the later pieces of a
        // fragmented stream, or a directory's index. Every want asks for something, so one of those,
        // or one that cannot be read, would only fail its owner as a record never kept does.
        if (extension is { Readable: true, Supplied: not MftAttributeKinds.None } kept)
        {
            lock (_extensions)
            {
                _extensions.Add(kept);
            }
        }

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

        return onRecord(number, outcome, in parsed, name) ? MftHandOn.Done : MftHandOn.Abandoned;
    }
}
