namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The parsed table as parallel arrays, indexed by record number.
///
/// Parallel arrays rather than an array of records because this is sized by the volume: a
/// real disk runs to millions of entries, and one object per record would cost more in
/// headers alone than every field here put together.
/// </summary>
public sealed class MftVolumeTree(int count)
{
    public int Count { get; } = count;

    public uint[] Parent { get; } = new uint[count];

    public long[] Allocated { get; } = new long[count];

    public long[] Logical { get; } = new long[count];

    public bool[] IsDirectory { get; } = new bool[count];

    /// <summary>
    /// Records whose size the table did not establish. One of these anywhere in a subtree makes
    /// that subtree unmeasurable — the alternative is a total that is short by an unknown amount
    /// and indistinguishable from a correct one.
    /// </summary>
    public bool[] SizeUnknown { get; } = new bool[count];

    /// <summary>
    /// Entries that are links to somewhere else. The walk never enters one, so neither may
    /// anything derived from this tree — and a link's own subtree here is empty, which would
    /// otherwise total a populated directory as clear.
    /// </summary>
    public bool[] IsReparsePoint { get; } = new bool[count];

    /// <summary>
    /// The newer of each record's creation and last-write times, as a FILETIME.
    ///
    /// <para>One array rather than two, because the two are never wanted apart: the only question
    /// asked of them is <see cref="Safety.MinimumAge.Protects(long, long)"/>, which takes the newer
    /// of the pair. Derived once here, where the record is already in hand, it costs eight bytes a
    /// record instead of sixteen — and this is sized by the volume, where a full disk runs to
    /// millions of entries.</para>
    ///
    /// <para>Left at zero for every record while no guard is in force, which is what an unset NTFS
    /// timestamp reads as anyway, and what <see cref="Safety.MinimumAge.Off"/> compares against to
    /// protect nothing.</para>
    /// </summary>
    public long[] Newest { get; } = new long[count];

    /// <summary>
    /// Names are kept for directories only. Path resolution never needs a file's name — a subtree
    /// total is the sum of its records regardless of what they are called — and skipping them is
    /// the difference between tens of megabytes of strings and hundreds on a full volume.
    /// </summary>
    public string?[] Names { get; } = new string?[count];

    /// <summary>
    /// Which records the table actually described. Records not present are neither summed nor
    /// linked: without this a free or sparse region reads as a forest of record-zero children,
    /// which would attach the whole volume to the root a second time.
    /// </summary>
    public bool[] Present { get; } = new bool[count];

    public void Set(long number, MftRecord record)
    {
        Parent[number] = record.ParentRecordNumber;
        Allocated[number] = record.Size?.Allocated ?? 0;
        Logical[number] = record.Size?.Logical ?? 0;
        SizeUnknown[number] = record.Size is null;
        IsDirectory[number] = record.IsDirectory;
        IsReparsePoint[number] = record.IsReparsePoint;
        Newest[number] = Math.Max(record.CreatedFileTime, record.LastWrittenFileTime);
        Present[number] = true;

        if (record.IsDirectory)
        {
            Names[number] = record.Name;
        }
    }

    /// <summary>
    /// Whether this record should become a child of its parent. The root is its own parent, so
    /// linking it would make the tree cyclic and a walk from the root would never terminate.
    /// </summary>
    public bool IsLinkable(int number) => Present[number] && number != MftRecord.RootRecordNumber;
}

/// <summary>
/// Children in compressed-row form: <c>Start[p]</c> to <c>Start[p + 1]</c> indexes into
/// <c>Children</c>.
/// </summary>
public sealed record MftChildLinks(int[] Start, uint[] Children);
