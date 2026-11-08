namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A file's other names (<see cref="MftLink"/>), each in the directory that holds it, as a record
/// handler is given them beside the name the file is placed by.
///
/// <para>Good only while the handler runs, like the name beside it. A record read in one pass hands
/// its names on as they sit in the buffer it was read into, which is read into again once the handler
/// returns, so a handler copies only the names it keeps. Building a volume index keeps almost none of
/// them, and a system volume's component store links each of its files into several folders. A record
/// that had to outlive its buffer, one whose attributes continue in extension records, has decoded its
/// names already and hands those on.</para>
/// </summary>
public readonly ref struct MftOtherLinks
{
    private readonly ReadOnlySpan<byte> _record;
    private readonly int _firstAttributeOffset;
    private readonly int _placedBy;
    private readonly IReadOnlyList<MftLink>? _kept;

    /// <summary>
    /// The links in <paramref name="record"/>: every name in it that is not an 8.3 alias, except the
    /// one whose characters start at <paramref name="placedBy"/>.
    /// </summary>
    /// <param name="count">How many there are, which the parse counted as it ranked them.</param>
    internal MftOtherLinks(ReadOnlySpan<byte> record, int firstAttributeOffset, int placedBy, int count)
    {
        _record = record;
        _firstAttributeOffset = firstAttributeOffset;
        _placedBy = placedBy;
        Count = count;
    }

    /// <summary>Links already decoded, or null for none.</summary>
    internal MftOtherLinks(IReadOnlyList<MftLink>? kept)
    {
        _kept = kept;
        Count = kept?.Count ?? 0;
    }

    /// <summary>A file with no name beside the one it is placed by, as almost every file is.</summary>
    public static MftOtherLinks None => default;

    public int Count { get; }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Reads the record's names again rather than remembering where each link sat, because a file
    /// can have hundreds and the parse that ranked them allocates nothing. Stops at the last link, and
    /// reads nothing at all for a file with none.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly MftOtherLinks _links;
        private MftAttributeEnumerator _walk;
        private int _offset;
        private int _yielded;

        internal Enumerator(MftOtherLinks links)
        {
            _links = links;
            _walk = new MftAttributeEnumerator(links._record, links._firstAttributeOffset);
            _offset = links._firstAttributeOffset;
        }

        public MftLinkName Current { get; private set; }

        public bool MoveNext()
        {
            if (_yielded == _links.Count)
            {
                return false;
            }

            if (_links._kept is { } kept)
            {
                var link = kept[_yielded++];
                Current = new MftLinkName(link.ParentRecordNumber, link.Name);
                return true;
            }

            while (_walk.MoveNext())
            {
                var at = _offset;
                _offset += _walk.Current.Length;

                if (_walk.CurrentType == MftRecordParser.AttributeFileName
                    && MftRecordParser.TryReadFileName(_walk.Current, out var name)
                    && MftRecordParser.IsLink(MftRecordParser.RankOf(name.Namespace))
                    && at + name.NameOffset != _links._placedBy)
                {
                    _yielded++;
                    Current = new MftLinkName(name.Parent, MftRecordParser.NameAt(_links._record, at + name.NameOffset, name.NameLength));
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>One of a file's other names, as <see cref="MftOtherLinks"/> hands it on.</summary>
/// <param name="parentRecordNumber">Record number of the directory holding this name.</param>
/// <param name="name">The name, exactly as NTFS stored it, and good only while the handler runs.</param>
public readonly ref struct MftLinkName(uint parentRecordNumber, ReadOnlySpan<char> name)
{
    /// <summary>Record number of the directory holding this name.</summary>
    public uint ParentRecordNumber { get; } = parentRecordNumber;

    /// <summary>The name, exactly as NTFS stored it, and good only while the handler runs.</summary>
    public ReadOnlySpan<char> Name { get; } = name;
}
