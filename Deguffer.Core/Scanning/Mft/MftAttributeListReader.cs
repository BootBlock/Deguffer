namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the <c>$ATTRIBUTE_LIST</c>s the first pass found kept outside the table: several at once,
/// and in the order of their clusters, so the disk is kept busy and moves forward through the volume
/// rather than back and forth across it. Lists close together are read together, in reads
/// <see cref="MftListReadPlanner"/> plans, and the reads are made by <see cref="OrderedReads"/>, so a
/// read that lands starts the next one whichever read is slow.
///
/// <para><b>Each list is handed on in cluster order</b>, whatever order the reads complete in, so what
/// a table wanting more extension records than it holds loses is the same on every run. A list is
/// read out of its buffer as soon as its read lands, and its entries wait for their turn, at most
/// <see cref="RoundsAhead"/> rounds of the reads in flight.</para>
///
/// <para><b>A list is handed on as it would be read alone.</b> Where a read of several fails, each of
/// its lists is read again on its own, so an unreadable cluster loses only the list it is part of,
/// and a gap read across never loses a list at all.</para>
///
/// <para><b>Each read in flight has one buffer</b>, kept for the next read made in its place, so a
/// table with thousands of lists costs no more memory than one with a few, and no buffer is made per
/// read. A caller that reads lists in several batches lends the same buffers to each
/// (<see cref="MftListReadAhead"/>). No buffer is released while a read is in flight: every way out,
/// a cancellation or a failure included, first cancels the reads still in flight and waits for
/// them.</para>
/// </summary>
internal sealed class MftAttributeListReader
{
    /// <summary>
    /// How many rounds of the reads in flight may be made past the oldest read whose lists are not yet
    /// handed on. One slow read then holds back no other until the rest have gone round this many times
    /// behind it, and the entries waiting for their turn are bounded by this many rounds of reads (see
    /// <see cref="TableTuning"/>).
    /// </summary>
    private const int RoundsAhead = 4;

    private readonly IMftSource _source;
    private readonly List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)> _lists;
    private readonly IReadOnlyList<MftListRead> _plan;
    private readonly int _clusterBytes;

    /// <summary>
    /// The buffer each place for a read in flight keeps, touched only by the read made in it, and
    /// released by whoever made the array, once nothing is in flight.
    /// </summary>
    private readonly VolumeReadBuffer?[] _buffers;

    /// <summary>Each list's entries, from when its read lands until it is handed on.</summary>
    private readonly IReadOnlyList<MftAttributeListEntry>?[] _entries;

    private MftAttributeListReader(
        IMftSource source,
        List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)> lists,
        VolumeReadBuffer?[] buffers)
    {
        _source = source;
        _lists = lists;
        _clusterBytes = source.BytesPerCluster;
        _plan = MftListReadPlanner.Plan(lists.ConvertAll(static l => (l.Runs, l.Length)), _clusterBytes);
        _buffers = buffers;
        _entries = new IReadOnlyList<MftAttributeListEntry>?[lists.Count];
    }

    /// <summary>
    /// Read every list still pending in <paramref name="deferred"/>, and hand each on with its
    /// record. A list not handed on, because <paramref name="onList"/> asked to stop, stays pending.
    /// </summary>
    /// <param name="onList">
    /// Given a record and its list's entries, null where the list could not be read, on the calling
    /// thread and in cluster order. Returns false to read no more.
    /// </param>
    public static void Read(
        IMftSource source,
        IReadOnlyList<MftDeferredRecord> deferred,
        int readsInFlight,
        Func<MftDeferredRecord, IReadOnlyList<MftAttributeListEntry>?, bool> onList,
        CancellationToken ct)
    {
        var buffers = new VolumeReadBuffer?[readsInFlight];

        try
        {
            Read(source, deferred, buffers, onList, ct);
        }
        finally
        {
            Release(buffers);
        }
    }

    /// <summary>
    /// <see cref="Read(IMftSource, IReadOnlyList{MftDeferredRecord}, int, Func{MftDeferredRecord, IReadOnlyList{MftAttributeListEntry}?, bool}, CancellationToken)"/>
    /// into <paramref name="buffers"/>, one per read in flight, which the caller keeps for its next
    /// call and releases with <see cref="Release"/>. Nothing is in flight in them once this returns.
    /// </summary>
    public static void Read(
        IMftSource source,
        IReadOnlyList<MftDeferredRecord> deferred,
        VolumeReadBuffer?[] buffers,
        Func<MftDeferredRecord, IReadOnlyList<MftAttributeListEntry>?, bool> onList,
        CancellationToken ct)
    {
        new MftAttributeListReader(source, InReadOrder(deferred), buffers).HandOn(onList, ct);
    }

    /// <summary>
    /// The records of <paramref name="deferred"/> whose lists are still pending, with where each list
    /// is, in the order
    /// <see cref="Read(IMftSource, IReadOnlyList{MftDeferredRecord}, int, Func{MftDeferredRecord, IReadOnlyList{MftAttributeListEntry}?, bool}, CancellationToken)"/>
    /// hands them on: by the first cluster of each list.
    /// </summary>
    public static List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)> InReadOrder(
        IReadOnlyList<MftDeferredRecord> deferred)
    {
        var pending = new List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)>();

        foreach (var record in deferred)
        {
            if (record.PendingList is { } list)
            {
                pending.Add((record, list.Runs, list.Length));
            }
        }

        // A list with no runs is never pending, so every one has a first cluster. Ties are damage,
        // and broken by record so the order is the same on every run.
        pending.Sort(static (a, b) => a.Runs[0].StartCluster != b.Runs[0].StartCluster
            ? a.Runs[0].StartCluster.CompareTo(b.Runs[0].StartCluster)
            : a.Record.Self.Record.CompareTo(b.Record.Self.Record));

        return pending;
    }

    /// <summary>Release the buffers lent to every read, once nothing is in flight in them.</summary>
    public static void Release(VolumeReadBuffer?[] buffers)
    {
        foreach (var buffer in buffers)
        {
            ((IDisposable?)buffer)?.Dispose();
        }
    }

    private void HandOn(Func<MftDeferredRecord, IReadOnlyList<MftAttributeListEntry>?, bool> onList, CancellationToken ct)
    {
        // Disposing the reads waits for every read still in flight, so the buffers are free after it.
        using var reads = new OrderedReads(_plan.Count, _buffers.Length, _buffers.Length * RoundsAhead, LandAsync, ct);

        for (var read = 0; read < _plan.Count; read++)
        {
            reads.Await(read);

            var made = _plan[read];

            for (var i = made.First; i < made.First + made.Count; i++)
            {
                var entries = _entries[i];
                _entries[i] = null;

                if (!onList(_lists[i].Record, entries))
                {
                    return;
                }
            }

            reads.HandedOn();
        }
    }

    /// <summary>Make read <paramref name="index"/> into the buffer of <paramref name="place"/>, and read each of its lists out of it.</summary>
    private async Task LandAsync(int place, int index, CancellationToken ct)
    {
        var read = _plan[index];
        var buffer = BufferFor(place, read.Bytes);
        var whole = read.Count == 1
            ? await ReadAlone(_lists[read.First], buffer, 0, ct).ConfigureAwait(false)
            : await _source.TryReadClustersAsync(read.Cluster, buffer.Memory[..read.Bytes], ct).ConfigureAwait(false);

        for (var i = read.First; i < read.First + read.Count; i++)
        {
            var (_, runs, length) = _lists[i];
            var offset = read.Count == 1 ? 0 : (int)(runs[0].StartCluster - read.Cluster) * _clusterBytes;

            // Read again alone, one after another: a read of several fails only where a cluster in it
            // cannot be read, which is rare, and the other reads stay in flight meanwhile.
            var readable = whole
                || (read.Count > 1 && await ReadAlone(_lists[i], buffer, offset, ct).ConfigureAwait(false));

            _entries[i] = readable ? MftAttributeList.TryReadEntries(buffer.Span.Slice(offset, length)) : null;
        }
    }

    /// <summary>Read one list by its own runs into <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    private Task<bool> ReadAlone(
        (MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length) list,
        VolumeReadBuffer buffer,
        int offset,
        CancellationToken ct) =>
        MftClusterValue.TryReadAsync(
            _source, list.Runs, list.Length, buffer.Memory.Slice(offset, MftClusterValue.BytesFor(_clusterBytes, list.Length)), ct);

    /// <summary>The buffer kept in <paramref name="place"/>, replaced by a larger one where it cannot hold <paramref name="bytes"/>.</summary>
    private VolumeReadBuffer BufferFor(int place, int bytes)
    {
        if (_buffers[place] is { } kept && kept.Length >= bytes)
        {
            return kept;
        }

        ((IDisposable?)_buffers[place])?.Dispose();
        _buffers[place] = null;

        return _buffers[place] = new VolumeReadBuffer(bytes);
    }
}
