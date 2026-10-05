namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A base record whose <c>$ATTRIBUTE_LIST</c> sends the reader to other records for something the
/// scan needs: its name, the sizes of its data, or its reparse point.
///
/// <para>NTFS does this once a file's attributes outgrow one record. A large fragmented file keeps
/// its <c>$DATA</c> in an extension record, and a file with many hard links keeps its names there.
/// The parser cannot follow the list itself — it is a pure function over one record — so it hands
/// this over, and <see cref="MftExtensionReader"/> fills it in once the first pass is over.</para>
///
/// <para>Only what the scan needs is asked for. The list names every attribute the file has, and
/// most of them — later pieces of a fragmented stream, a directory's index — say nothing about
/// where the file sits or how large it is.</para>
/// </summary>
internal sealed class MftDeferredRecord
{
    private readonly bool _isDirectory;
    private MftRecordDraft _draft;
    private readonly List<(MftSegmentReference Segment, MftAttributeKinds Needs)> _segments = [];
    private bool _declaresUnnamedData;

    public MftDeferredRecord(long number, ushort sequence, bool isDirectory, MftRecordDraft draft)
    {
        Self = new MftSegmentReference(number, sequence);
        _isDirectory = isDirectory;
        _draft = draft;
    }

    /// <summary>The base record itself, as the extension records it owns must name it.</summary>
    public MftSegmentReference Self { get; }

    /// <summary>
    /// Where a non-resident list's bytes are, while they are still to be read. Null once the list
    /// has been read, or where it was resident and so read with the record.
    /// </summary>
    public (IReadOnlyList<DataRun> Runs, int Length)? PendingList { get; private set; }

    /// <summary>The extension records still to be read, and what each must supply.</summary>
    public IReadOnlyList<(MftSegmentReference Segment, MftAttributeKinds Needs)> Segments => _segments;

    /// <summary>The kinds of attribute the scan needed and could not establish.</summary>
    public MftAttributeKinds Lost { get; private set; }

    /// <summary>Whether something the scan needs could not be established.</summary>
    public bool Failed => Lost != MftAttributeKinds.None;

    /// <summary>Whether nothing further has to be read before <see cref="Finish"/> can answer.</summary>
    public bool IsComplete => Failed || (PendingList is null && _segments.Count == 0);

    public void AwaitList(IReadOnlyList<DataRun> runs, int length) => PendingList = (runs, length);

    /// <summary>
    /// Work out from the list's entries which extension records have to be read, or fail where the
    /// list could not be read. Null entries are a list that could not be read.
    /// </summary>
    public void Follow(IReadOnlyList<MftAttributeListEntry>? entries)
    {
        PendingList = null;

        if (entries is null)
        {
            FailList();
            return;
        }

        var wanted = new List<(MftSegmentReference Segment, MftAttributeKinds Needs)>();

        foreach (var entry in entries)
        {
            var needs = NeedsOf(entry);
            var elsewhere = entry.Segment.Record != Self.Record;

            // An entry naming the base record itself is already read, but it still has to name the
            // base record as it is now. A list that does not is one caught mid-change.
            if (!elsewhere && entry.Segment.Sequence != Self.Sequence)
            {
                FailList();
                return;
            }

            if (elsewhere && needs != MftAttributeKinds.None)
            {
                wanted.Add((entry.Segment, needs));
            }
        }

        // One read per record, however many entries name it. Ordered by record so names found in
        // extension records are ranked in a stable order: the base record first, then the rest
        // from the start of the table. Merged by reference rather than by number, so two entries
        // naming one record by two sequence numbers stay two, and the one the record does not
        // carry fails as any stale reference does.
        wanted.Sort(static (a, b) => a.Segment.Record != b.Segment.Record
            ? a.Segment.Record.CompareTo(b.Segment.Record)
            : a.Segment.Sequence.CompareTo(b.Segment.Sequence));

        foreach (var (segment, needs) in wanted)
        {
            if (_segments.Count > 0 && _segments[^1].Segment == segment)
            {
                _segments[^1] = (segment, _segments[^1].Needs | needs);
                continue;
            }

            _segments.Add((segment, needs));
        }
    }

    /// <summary>
    /// Fold in one extension record this needed. It is accepted only where it is still what the
    /// list says it is: in use, owned by this base record as it is now, carrying the sequence number
    /// the list recorded, and holding what it was read for. Anything else is a record caught
    /// mid-change, and the answer stays "could not establish", never a guess.
    /// </summary>
    /// <param name="record">The record's bytes, which the update sequence fixup modifies in place.</param>
    public void Absorb(Span<byte> record, int bytesPerSector, MftSegmentReference expected, MftAttributeKinds needs)
    {
        // Folded into a copy, kept only once the record is accepted. A record rejected for lacking
        // what it was read for can still hold a better-ranked name or a reparse point, and either
        // left in the draft would place the file, or mark it a link, on the word of a record this
        // has just refused to believe.
        var trial = _draft;

        if (MftRecordHeader.ReadExtension(record, bytesPerSector, out var header) != MftParseOutcome.Parsed
            || header.BaseReference != Self
            || header.Sequence != expected.Sequence
            || !trial.TryAbsorb(record[..header.UsedLength], header.FirstAttributeOffset, isBase: false, out var supplied, out _)
            || (needs & ~supplied) != MftAttributeKinds.None)
        {
            Fail(needs);
            return;
        }

        _draft = trial;
    }

    /// <summary>
    /// Record that an extension record this needed for <paramref name="needs"/> could not be
    /// established, and lose exactly that. Only a record wanted for a name puts the file's place in
    /// doubt: one wanted for the size alone loses the size and nothing else, so a growing file caught
    /// mid-change on a live volume reads as a size unknown under its own directory, not as a file
    /// that could be anywhere.
    /// </summary>
    public void Fail(MftAttributeKinds needs) => Lost |= needs;

    public MftParseOutcome Finish(out MftRecord result) =>
        _draft.Finish(_isDirectory, new MftListFindings(_declaresUnnamedData, Lost), out result);

    /// <summary>
    /// The list itself could not be trusted, so anything the base record did not settle could have
    /// been anywhere. A file has one reparse point, and the piece of its stream starting at cluster 0
    /// states the whole stream's sizes, so either one held by the base record is settled; a name
    /// is settled only where nothing could displace it.
    /// </summary>
    private void FailList() =>
        Fail(((MftAttributeKinds.DataStart | MftAttributeKinds.ReparsePoint) & ~_draft.BaseSupplied)
            | (_draft.BaseRank != 0 ? MftAttributeKinds.Name : MftAttributeKinds.None));

    /// <summary>
    /// What one entry would have to supply, judged by what the base record already settled.
    ///
    /// <para>A name is wanted only where the base record's own best name could be displaced. One
    /// already in the best namespace cannot be, and a tie goes to the base record.</para>
    ///
    /// <para>Only the piece of the stream starting at cluster 0 is wanted, because only that piece
    /// states the stream's allocated, data and valid sizes. A fragmented file can list hundreds of
    /// pieces, and reading the rest would learn nothing. A directory's own stream is never counted,
    /// so its pieces are not wanted at all.</para>
    /// </summary>
    private MftAttributeKinds NeedsOf(MftAttributeListEntry entry)
    {
        switch (entry.Type)
        {
            case MftRecordParser.AttributeFileName when _draft.BaseRank != 0:
                return MftAttributeKinds.Name;

            case MftRecordParser.AttributeData when !entry.IsNamed:
                _declaresUnnamedData = true;
                return entry.LowestVcn == 0 && !_isDirectory ? MftAttributeKinds.DataStart : MftAttributeKinds.None;

            case MftRecordParser.AttributeReparsePoint:
                return MftAttributeKinds.ReparsePoint;

            default:
                return MftAttributeKinds.None;
        }
    }
}
