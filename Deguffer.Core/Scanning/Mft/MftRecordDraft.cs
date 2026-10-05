namespace Deguffer.Core.Scanning.Mft;

/// <summary>The kinds of attribute a scan needs, as an extension record may be asked to supply them.</summary>
[Flags]
internal enum MftAttributeKinds
{
    None = 0,

    /// <summary>A <c>$FILE_NAME</c>, which places the file under its parent.</summary>
    Name = 1,

    /// <summary>The piece of the unnamed <c>$DATA</c> starting at cluster 0, the only one stating its sizes.</summary>
    DataStart = 2,

    /// <summary>A <c>$REPARSE_POINT</c>, which can make the file a link rather than content.</summary>
    ReparsePoint = 4,
}

/// <summary>
/// What one file's attributes have said so far, gathered from its base record and from any
/// extension records its <c>$ATTRIBUTE_LIST</c> sends a reader to.
///
/// <para>One set of rules for both, because a file whose attributes are split across records is
/// the same file: a <c>$FILE_NAME</c> found in an extension record is ranked exactly as one in the
/// base record would be, and a <c>$DATA</c> piece is judged the same way wherever it sits.</para>
///
/// <para>A struct, because the first pass builds one for every record in the table and almost all
/// of them are finished on the spot.</para>
/// </summary>
internal struct MftRecordDraft
{
    private const int NoName = int.MaxValue;

    private uint _parent;
    private string _name;
    private int _bestRank;
    private ScanSize? _size;
    private bool _sawUnnamedData;
    private bool _isReparsePoint;
    private long _created;
    private long _lastWritten;

    public MftRecordDraft()
    {
        _name = string.Empty;
        _bestRank = NoName;
        BaseRank = NoName;
    }

    /// <summary>
    /// The rank of the best name the base record held on its own. Zero means no name anywhere else
    /// can displace it, so a list's other names need not be read to place the file.
    /// </summary>
    public int BaseRank { get; private set; }

    /// <summary>
    /// Fold one record's attributes into what is known, saying which kinds it supplied. Returns
    /// false where the attributes are malformed, and a record that fails here must be rejected
    /// whole rather than kept in part: a partly read record reports a plausible wrong size.
    /// </summary>
    /// <param name="attributeList">
    /// Where the base record keeps its <c>$ATTRIBUTE_LIST</c>, as an offset and a length within
    /// <paramref name="record"/>; null if it has none. Always null for an extension record, whose
    /// owner's list is the only one.
    /// </param>
    public bool TryAbsorb(
        ReadOnlySpan<byte> record,
        int firstAttributeOffset,
        bool isBase,
        out MftAttributeKinds supplied,
        out (int Offset, int Length)? attributeList)
    {
        supplied = MftAttributeKinds.None;
        attributeList = null;

        var walk = new MftAttributeEnumerator(record, firstAttributeOffset);
        var offset = firstAttributeOffset;

        while (walk.MoveNext())
        {
            switch (walk.CurrentType)
            {
                // Where the dates come from, and the only place they may. NTFS keeps a second copy
                // of all four times inside $FILE_NAME, and refreshes that copy when the name
                // changes rather than when the file does — so a project rebuilt every day since it
                // was last renamed reports the date of the rename there. The same trap the
                // reparse-point flag beside the name sets, and the same answer: read the structure
                // that is the thing itself. Always in the base record, so an extension record's is
                // not one to trust.
                case MftRecordParser.AttributeStandardInformation when isBase:
                    (_created, _lastWritten) = MftRecordParser.ReadTimestamps(walk.Current);
                    break;

                case MftRecordParser.AttributeFileName when MftRecordParser.TryReadFileName(walk.Current, out var candidate):
                    supplied |= MftAttributeKinds.Name;

                    // Prefer the Win32 name over the 8.3 alias: a long-named file carries several
                    // $FILE_NAME attributes, and picking the DOS alias would make path resolution
                    // fail against the name the user actually typed. Strictly better only, so a tie
                    // keeps the name read first, and the base record is always read first.
                    var rank = MftRecordParser.RankOf(candidate.Namespace);
                    if (rank < _bestRank)
                    {
                        (_parent, _name, _bestRank) = (candidate.Parent, candidate.Name, rank);
                    }

                    break;

                case MftRecordParser.AttributeList when isBase:
                    attributeList = (offset, walk.Current.Length);
                    break;

                // The structure that makes an entry a junction or a link, rather than the flag for
                // it kept beside the name: NTFS refreshes those flags when the name changes rather
                // than when the file does, so a junction made over an existing directory can still
                // read as an ordinary one there. The attribute is the thing itself.
                case MftRecordParser.AttributeReparsePoint:
                    supplied |= MftAttributeKinds.ReparsePoint;
                    _isReparsePoint |= MftRecordParser.IsNameSurrogate(walk.Current);
                    break;

                case MftRecordParser.AttributeData when MftRecordParser.IsUnnamed(walk.Current):
                    _sawUnnamedData = true;

                    // The first piece that establishes a size is the one to keep. A file split
                    // across pieces states its sizes only in the one starting at cluster 0, and the
                    // continuations declare nothing. Assigning each in turn would let a
                    // continuation erase what an earlier record had already established.
                    if (MftRecordParser.ReadDataSize(walk.Current) is { } size)
                    {
                        supplied |= MftAttributeKinds.DataStart;
                        _size ??= size;
                    }

                    break;
            }

            offset += walk.Current.Length;
        }

        if (isBase)
        {
            BaseRank = _bestRank;
        }

        return !walk.IsMalformed;
    }

    /// <summary>
    /// The record this draft describes, or why there is none.
    /// </summary>
    /// <param name="list">
    /// What following the base record's <c>$ATTRIBUTE_LIST</c> established, or null where it has
    /// none. Without a list, every attribute the file has was in the base record.
    /// </param>
    public readonly MftParseOutcome Finish(bool isDirectory, MftListFindings? list, out MftRecord result)
    {
        result = default;

        // No name anywhere: a record in use claiming no identity. Without a list it points nowhere
        // else for one, which no healthy volume produces. With one, the names were in records that
        // could not be established — caught mid-change on a live volume, or damaged — and a file
        // that cannot be placed could belong to any total on the volume.
        //
        // The same holds where the base record's own name could have been displaced by one in an
        // extension record that could not be read. Placing the file under its second-best name
        // is a guess at which directory it belongs to.
        if (_bestRank == NoName || (list is { Failed: true } && BaseRank != 0))
        {
            return MftParseOutcome.Unreadable;
        }

        result = new MftRecord(_parent, _name, SizeFor(isDirectory, list), isDirectory, _isReparsePoint, _created, _lastWritten);
        return MftParseOutcome.Parsed;
    }

    private readonly ScanSize? SizeFor(bool isDirectory, MftListFindings? list)
    {
        // A directory's own $DATA is not the size of its contents — the contents are counted
        // through their own records — so attributing anything here would double-count them.
        // Nothing is read from it, so a directory that keeps its attributes elsewhere is still a
        // known quantity: zero. Refusing there would give up on every large directory on the volume,
        // which is precisely where NTFS runs out of room in a record.
        if (isDirectory)
        {
            return ScanSize.Zero;
        }

        if (list is { Failed: true })
        {
            return null;
        }

        if (_size is { } size)
        {
            return size;
        }

        // Every null below is a file whose real size is somewhere this reader did not reach, and
        // returning zero would silently subtract it from whatever subtree it belongs to. A piece of
        // the stream with no sizes in it is one; a list naming the stream is another. With neither,
        // there is genuinely no unnamed stream — a symbolic link, say — and zero is the true answer.
        return _sawUnnamedData || list is { DeclaresUnnamedData: true } ? null : ScanSize.Zero;
    }
}

/// <summary>What following a base record's <c>$ATTRIBUTE_LIST</c> established.</summary>
/// <param name="DeclaresUnnamedData">Whether the list names an unnamed <c>$DATA</c> at all.</param>
/// <param name="Failed">
/// Whether the list, or a record it named and the scan needed, could not be established. Never a
/// smaller answer: what could not be read may be the very attribute the scan wanted.
/// </param>
internal readonly record struct MftListFindings(bool DeclaresUnnamedData, bool Failed);
