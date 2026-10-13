using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Places the entries a measurement leaves out as records of one volume's table, so the sum can step
/// over each without entering it. See <see cref="ScanBounds.LeftOut"/>.
///
/// <para>Apart from <see cref="MftVolumeIndex"/> because it answers a different question: which
/// record a path names, by name for a folder and by file number for a file, where the index totals a
/// subtree it has already found. Built for one measurement and dropped with it.</para>
/// </summary>
/// <param name="resolve">How the index finds the directory at a path below the volume root.</param>
internal sealed class MftLeftOutPlacement(
    MftVolumeTree tree,
    MftChildLinks links,
    Func<IReadOnlyList<string>, uint?> resolve)
{
    /// <summary>
    /// The record of each entry of <paramref name="leftOut"/>, given as components below
    /// <paramref name="root"/>, for <see cref="MftVolumeIndex.TryMeasure(IReadOnlyList{string}, MinimumAge, bool, IReadOnlySet{uint}, out bool, out IReadOnlyList{IReadOnlyList{string}})"/>
    /// to leave out. Null where an entry the subtree holds cannot be placed, which sends the caller to
    /// the walk: a total that counted an entry it was told to leave out would promise what the
    /// removal will not take.
    ///
    /// <para><b>A folder is found by its name, and a file by its number.</b> The table names
    /// directories alone, because naming every file on a volume would cost a string a record. A file
    /// is asked for through <paramref name="describe"/> instead, and its number is its record: one
    /// handle a file, for the few files a caller leaves out. The number is believed only where the
    /// table puts that record in the folder the path names, because a table read before the file was
    /// written may hold another file at that number, or nothing.</para>
    ///
    /// <para><b>Each folder's names are read once, into a map.</b> The entries left out of a
    /// temporary folder are mostly its own children, thousands of them in a folder of tens of
    /// thousands, and the index scans the children a name at a time. A name is
    /// matched as it matches one: exactly where it can be, and otherwise ignoring case, taking the
    /// first child in record order.</para>
    ///
    /// <para><b>An entry whose folder the table does not hold is passed over</b>, not refused. Whatever
    /// the reason, a folder outside the table is outside the subtree this table totals, so the entry
    /// is outside the total already. So is an entry Windows says is gone.</para>
    /// </summary>
    /// <param name="describe">
    /// How the entry at a path, given as components below the volume root, is described from the
    /// disk.
    /// </param>
    public IReadOnlySet<uint>? TryPlace(
        IReadOnlyList<string> root,
        IReadOnlyList<IReadOnlyList<string>> leftOut,
        Func<IReadOnlyList<string>, FileReading> describe)
    {
        var records = new HashSet<uint>();
        var folders = new Dictionary<string, (uint Record, ChildNames Names)?>(StringComparer.Ordinal);

        foreach (var below in leftOut)
        {
            var key = string.Join(Path.DirectorySeparatorChar, below.Take(below.Count - 1));

            if (!folders.TryGetValue(key, out var folder))
            {
                folders[key] = folder = resolve([.. root, .. below.Take(below.Count - 1)]) is { } found
                    ? (found, ChildNamesOf(found))
                    : null;
            }

            if (folder is not { } holding)
            {
                continue;
            }

            if (holding.Names.Find(below[^1]) is { } named)
            {
                records.Add(named);
                continue;
            }

            var reading = describe([.. root, .. below]);

            if (reading.Result is FileReadingResult.Gone)
            {
                continue;
            }

            if (reading.Description is not { } described
                || RecordOf(described.Identity) is not { } record
                || record == holding.Record
                || tree.Parent[record] != holding.Record)
            {
                return null;
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>The named children of one folder, as the index finds a child by its name.</summary>
    private ChildNames ChildNamesOf(uint directory)
    {
        var exact = new Dictionary<string, uint>(StringComparer.Ordinal);
        var folded = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        for (var i = links.Start[directory]; i < links.Start[directory + 1]; i++)
        {
            var child = links.Children[i];

            if (tree.Names[child] is { } name)
            {
                exact.TryAdd(name, child);
                folded.TryAdd(name, child);
            }
        }

        return new ChildNames(exact, folded);
    }

    /// <summary>One folder's named children, by their exact names and by their names ignoring case.</summary>
    private sealed record ChildNames(Dictionary<string, uint> Exact, Dictionary<string, uint> Folded)
    {
        public uint? Find(string name) =>
            Exact.TryGetValue(name, out var child) || Folded.TryGetValue(name, out child) ? child : null;
    }

    /// <summary>
    /// The record an NTFS file number names: its low 48 bits, the rest being the record's sequence
    /// number. Null where that is not a record of this table.
    /// </summary>
    private uint? RecordOf(FileIdentity identity)
    {
        var record = (ulong)identity.File & 0xFFFF_FFFF_FFFF;

        return record < (ulong)tree.Count ? (uint)record : null;
    }
}
