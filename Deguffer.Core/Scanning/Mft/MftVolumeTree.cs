using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The parsed table as parallel arrays, indexed by record number, and the hard links that place a
/// file under more than one directory.
///
/// Parallel arrays rather than an array of records because this is sized by the volume: a
/// real disk runs to millions of entries, and one object per record would cost more in
/// headers alone than every field here put together.
///
/// <para><b>A node is one name.</b> Nodes below <see cref="Count"/> are records, each under the name
/// it was placed by. Each of a file's other names (<see cref="MftLink"/>) is a node of its own past
/// them, which reads its record's size, dates and flags and keeps its own directory and, where it is
/// a mail store, its own name. So a subtree total meets a file once for each name inside it, as the
/// walk does, and every question here is asked of a node.</para>
///
/// <para>Filled from several threads at once, one record each. Every array slot belongs to one
/// record, so no two threads write the same one. The mail-store names and the links are what
/// records share, and they are kept in structures safe to share until <see cref="PlaceLinks"/>
/// fixes the links as nodes, once every record is in.</para>
///
/// <para>A record's flags are one byte, written in one store by <see cref="Set"/>. A
/// read-modify-write of that byte would be safe only while one thread owns the record, and a
/// single store needs no such argument.</para>
/// </summary>
public sealed class MftVolumeTree(int count)
{
    private readonly uint[] _parent = new uint[count];

    private readonly long[] _allocated = new long[count];

    private readonly long[] _logical = new long[count];

    /// <summary>
    /// The newer of each record's creation and last-write times, as a FILETIME, for
    /// <see cref="Safety.MinimumAge"/> to judge.
    ///
    /// <para>One array rather than two, because the two are never wanted apart. Reducing the pair is
    /// the guard's own rule, so <see cref="Safety.MinimumAge.NewestFileTimeOf(long, long)"/> does it
    /// rather than this type — and doing it here, where the record is already in hand, costs eight
    /// bytes a record instead of sixteen. This structure is sized by the volume, where a full disk
    /// runs to millions of entries.</para>
    ///
    /// <para>Populated for every record, whether or not a guard is ever asked about it. The table is
    /// built once per volume for a planning pass, and the pass asks it with the guard off as well as
    /// on (<see cref="AgedScan"/>), so neither reading may need it rebuilt. A record whose <c>$STANDARD_INFORMATION</c> the parser
    /// could not read lands at zero, which is the start of the NTFS epoch and older than any window
    /// — so its bytes count as reclaimable. That is the fail-open direction, and it is confined to a
    /// size estimate: nothing deletes from this table, and both removers re-read the real timestamp
    /// through <see cref="Safety.IFileSystem"/> before they touch anything.</para>
    /// </summary>
    private readonly long[] _newest = new long[count];

    /// <summary>
    /// Names are kept for directories only. Path resolution never needs a file's name — a subtree
    /// total is the sum of its records regardless of what they are called — and skipping them is
    /// the difference between tens of megabytes of strings and hundreds on a full volume.
    ///
    /// <para>The one exception is kept apart, in <see cref="_mailStoreNames"/>, so that everything
    /// reading this array can go on assuming it names directories alone.</para>
    /// </summary>
    private readonly string?[] _names = new string?[count];

    private readonly MftRecordFlags[] _flags = new MftRecordFlags[count];

    private readonly ConcurrentDictionary<uint, string> _mailStoreNames = [];

    /// <summary>The links met while the table is read, in no order, until <see cref="PlaceLinks"/>.</summary>
    private readonly ConcurrentQueue<LinkNode> _met = [];

    private LinkNode[] _links = [];

    /// <summary>How many records the table holds, which is also the first node that is a link.</summary>
    public int Count { get; } = count;

    /// <summary>Every node: each record, then each of the links <see cref="PlaceLinks"/> fixed.</summary>
    public int NodeCount => Count + _links.Length;

    /// <summary>The directory holding this node's name.</summary>
    public uint ParentOf(long node) => node < Count ? _parent[node] : Link(node).Parent;

    /// <summary>The record this node names: itself, or the file a link is another name of.</summary>
    public uint RecordOf(long node) => node < Count ? (uint)node : Link(node).Record;

    /// <summary>The space this node's record occupies, which every name of a file reads alike.</summary>
    public long AllocatedOf(long node) => _allocated[RecordOf(node)];

    /// <summary>The length of this node's record.</summary>
    public long LogicalOf(long node) => _logical[RecordOf(node)];

    /// <summary>The newer of this node's record's two times. See <see cref="_newest"/>.</summary>
    public long NewestOf(long node) => _newest[RecordOf(node)];

    public bool IsDirectory(long node) => Has(node, MftRecordFlags.Directory);

    /// <summary>
    /// Whether the table did not establish this node's size. One of these anywhere in a subtree
    /// makes that subtree unmeasurable — the alternative is a total that is short by an unknown
    /// amount and indistinguishable from a correct one.
    /// </summary>
    public bool SizeUnknown(long node) => Has(node, MftRecordFlags.SizeUnknown);

    /// <summary>
    /// Whether this entry is a link to somewhere else. The walk never enters one, so neither may
    /// anything derived from this tree — and a link's own subtree here is empty, which would
    /// otherwise total a populated directory as clear.
    ///
    /// <para>A reparse point, not a hard link: every name of a file is the file itself, so a hard
    /// link answers this as its record does.</para>
    /// </summary>
    public bool IsReparsePoint(long node) => Has(node, MftRecordFlags.ReparsePoint);

    /// <summary>
    /// The name of the directory this node is, or null for a file. A link is always a file's: NTFS
    /// gives a directory no second name.
    /// </summary>
    public string? DirectoryName(long node) => node < Count ? _names[node] : null;

    /// <summary>
    /// Whether this node is an Outlook mail store, which no total may include (§9). A flag per
    /// record rather than a lookup, because a subtree total asks it of every file it sums. A link is
    /// judged by its own name, as the walk judges each name it lists.
    ///
    /// <para>Decided while the table is read, because it is the only moment a file's name is in
    /// hand: <see cref="DirectoryName"/> keeps no file names, so nothing afterwards could tell a
    /// store from any other file. See <see cref="MailStore"/>.</para>
    /// </summary>
    public bool IsMailStore(long node) =>
        node < Count ? Has(node, MftRecordFlags.MailStore) : Link(node).MailStoreName is not null;

    /// <summary>
    /// The name of a mail store, and null for any other node. A store's path has to be rebuilt so the
    /// plan can protect it, and a handful of names costs nothing beside the string per record that
    /// naming every file would.
    /// </summary>
    public string? MailStoreName(long node) =>
        node < Count ? _mailStoreNames.GetValueOrDefault((uint)node) : Link(node).MailStoreName;

    /// <summary>
    /// Whether the table actually described this record. Records not present are neither summed
    /// nor linked: without this a free or sparse region reads as a forest of record-zero children,
    /// which would attach the whole volume to the root a second time.
    ///
    /// <para>Read only while the child links are built, and kept after for nothing: as a bit of a
    /// byte every record has anyway, it costs nothing to keep.</para>
    /// </summary>
    public bool IsPresent(long node) => Has(node, MftRecordFlags.Present);

    /// <summary>
    /// Put one record in, under each of its names that is in the table.
    ///
    /// <para>A name whose directory is outside the table is dropped, and the file keeps its others.
    /// That is ordinary on a live volume: a directory removed mid-read, or a table that grew after
    /// its size was measured. Such a name is unreachable from the root, so it cannot be inside
    /// anything this tree is asked to total. Where the name the record was placed by is one of
    /// those, the first of its others in the table takes its place, and a record with none is left
    /// out.</para>
    /// </summary>
    /// <param name="name">The record's name, copied only where it is kept.</param>
    public void Set(long number, in MftRecord record, ReadOnlySpan<char> name)
    {
        var links = record.OtherLinks ?? [];
        var parent = record.ParentRecordNumber;
        var standIn = -1;

        if (parent >= (uint)Count)
        {
            for (var i = 0; i < links.Count && standIn < 0; i++)
            {
                standIn = links[i].ParentRecordNumber < (uint)Count ? i : -1;
            }

            if (standIn < 0)
            {
                return;
            }

            parent = links[standIn].ParentRecordNumber;
            name = links[standIn].Name;
        }

        _parent[number] = parent;
        _allocated[number] = record.Size?.Allocated ?? 0;
        _logical[number] = record.Size?.Logical ?? 0;
        _newest[number] = MinimumAge.NewestFileTimeOf(record.CreatedFileTime, record.LastWrittenFileTime);

        var flags = MftRecordFlags.Present
            | (record.Size is null ? MftRecordFlags.SizeUnknown : MftRecordFlags.None)
            | (record.IsDirectory ? MftRecordFlags.Directory : MftRecordFlags.None)
            | (record.IsReparsePoint ? MftRecordFlags.ReparsePoint : MftRecordFlags.None);

        if (record.IsDirectory)
        {
            _names[number] = name.ToString();
        }
        else if (MailStore.Is(name))
        {
            flags |= MftRecordFlags.MailStore;
            _mailStoreNames[(uint)number] = name.ToString();
        }

        _flags[number] = flags;

        for (var i = 0; i < links.Count; i++)
        {
            if (i != standIn && links[i].ParentRecordNumber < (uint)Count)
            {
                _met.Enqueue(new LinkNode(
                    (uint)number, links[i].ParentRecordNumber, MailStore.Is(links[i].Name) ? links[i].Name : null));
            }
        }
    }

    /// <summary>
    /// Make each link met a node, once every record is in. In record order, then by directory and
    /// mail-store name, so one table always numbers its links alike whatever order its threads met
    /// them in. Two links that tie on all three are the same value, so their order changes nothing.
    /// </summary>
    public void PlaceLinks() =>
        _links = [.. _met
            .OrderBy(link => link.Record)
            .ThenBy(link => link.Parent)
            .ThenBy(link => link.MailStoreName, StringComparer.Ordinal)];

    /// <summary>
    /// Whether this node should become a child of its parent. The root is its own parent, so
    /// linking it would make the tree cyclic and a walk from the root would never terminate. Every
    /// link placed is one whose record is in and whose directory is in the table.
    /// </summary>
    public bool IsLinkable(int node) => node >= Count || (IsPresent(node) && node != MftRecord.RootRecordNumber);

    private bool Has(long node, MftRecordFlags flag) => (_flags[RecordOf(node)] & flag) != 0;

    private LinkNode Link(long node) => _links[node - Count];

    /// <summary>One more name of a file, and the name itself only where it is a mail store's.</summary>
    private readonly record struct LinkNode(uint Record, uint Parent, string? MailStoreName);
}

/// <summary>
/// Children in compressed-row form: <c>Start[p]</c> to <c>Start[p + 1]</c> indexes into
/// <c>Children</c>. A child is a node of <see cref="MftVolumeTree"/>, so a file with several names
/// in one subtree is a child once for each.
/// </summary>
public sealed record MftChildLinks(int[] Start, uint[] Children);
