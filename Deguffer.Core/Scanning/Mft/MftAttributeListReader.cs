namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the <c>$ATTRIBUTE_LIST</c>s the first pass found kept outside the table: several at once,
/// and in the order of their clusters, so the disk is kept busy and moves forward through the volume
/// rather than back and forth across it. Lists close together are read together, in reads
/// <see cref="MftListReadPlanner"/> plans.
///
/// <para><b>Each list is handed on in that order</b>, whatever order the reads complete in, so what a
/// table wanting more extension records than it holds loses is the same on every run.</para>
///
/// <para><b>A list is handed on as it would be read alone.</b> Where a read of several fails, each of
/// its lists is read again on its own, so an unreadable cluster loses only the list it is part of,
/// and a gap read across never loses a list at all.</para>
///
/// <para><b>At most one read in flight is held</b>, each in a buffer kept for the next read into its
/// place, so a table with thousands of lists costs no more memory than one with a few, and no buffer
/// is made per read.</para>
///
/// <para><b>Nothing is released while a read is in flight.</b> A read lands in its buffer whenever the
/// disk finishes, so every way out, a cancellation or a failure included, first cancels the reads
/// still in flight and waits for them.</para>
/// </summary>
internal static class MftAttributeListReader
{
    /// <summary>
    /// Read every list still pending in <paramref name="deferred"/>, and hand each on with its
    /// record. A list not handed on, because <paramref name="onList"/> asked to stop, stays pending.
    /// </summary>
    /// <param name="onList">
    /// Given a record and its list's entries, null where the list could not be read. Returns false to
    /// read no more.
    /// </param>
    public static void Read(
        IMftSource source,
        IReadOnlyList<MftDeferredRecord> deferred,
        int readsInFlight,
        Func<MftDeferredRecord, IReadOnlyList<MftAttributeListEntry>?, bool> onList,
        CancellationToken ct)
    {
        var lists = new List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)>();

        foreach (var record in deferred)
        {
            if (record.PendingList is { } list)
            {
                lists.Add((record, list.Runs, list.Length));
            }
        }

        // A list with no runs is never pending, so every one has a first cluster. Ties are damage,
        // and broken by record so the order is the same on every run.
        lists.Sort(static (a, b) => a.Runs[0].StartCluster != b.Runs[0].StartCluster
            ? a.Runs[0].StartCluster.CompareTo(b.Runs[0].StartCluster)
            : a.Record.Self.Record.CompareTo(b.Record.Self.Record));

        var clusterBytes = source.BytesPerCluster;
        var plan = MftListReadPlanner.Plan(lists.ConvertAll(static l => (l.Runs, l.Length)), clusterBytes);
        var buffers = new VolumeReadBuffer?[readsInFlight];
        var reads = new Queue<(MftListRead Plan, VolumeReadBuffer Buffer, Task<bool> Read)>(readsInFlight);
        using var cancelReads = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var next = 0;

            while (next < plan.Count || reads.Count > 0)
            {
                // Read k is made into buffer k modulo the reads in flight, which read k minus that
                // many left once it completed and its lists were handed on.
                while (next < plan.Count && reads.Count < readsInFlight)
                {
                    ct.ThrowIfCancellationRequested();

                    var read = plan[next];
                    var buffer = BufferFor(buffers, next % readsInFlight, read.Bytes);

                    reads.Enqueue((read, buffer, read.Count == 1
                        ? ReadAlone(source, lists[read.First], buffer, 0, cancelReads.Token)
                        : source.TryReadClustersAsync(read.Cluster, buffer.Memory[..read.Bytes], cancelReads.Token).AsTask()));
                    next++;
                }

                var (made, held, landed) = reads.Dequeue();
                var whole = landed.GetAwaiter().GetResult();

                for (var i = made.First; i < made.First + made.Count; i++)
                {
                    var (record, runs, length) = lists[i];
                    var offset = made.Count == 1 ? 0 : (int)(runs[0].StartCluster - made.Cluster) * clusterBytes;

                    // Read again alone, one after another: a read of several fails only where a cluster
                    // in it cannot be read, which is rare, and the other reads stay in flight meanwhile.
                    var readable = whole
                        || (made.Count > 1 && ReadAlone(source, lists[i], held, offset, cancelReads.Token).GetAwaiter().GetResult());
                    var entries = readable ? MftAttributeList.TryReadEntries(held.Span.Slice(offset, length)) : null;

                    if (!onList(record, entries))
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            cancelReads.Cancel();

            foreach (var (_, _, pending) in reads)
            {
                // Waited for, never thrown: whatever is leaving here already says why, and a read
                // cancelled because nothing will look at it is not a second failure. Its exception
                // is read so it is not reported later as one nobody saw.
                Task.WaitAny(pending);
                _ = pending.Exception;
            }

            foreach (var buffer in buffers)
            {
                ((IDisposable?)buffer)?.Dispose();
            }
        }
    }

    /// <summary>Read one list by its own runs into <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    private static Task<bool> ReadAlone(
        IMftSource source,
        (MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length) list,
        VolumeReadBuffer buffer,
        int offset,
        CancellationToken ct) =>
        MftClusterValue.TryReadAsync(
            source, list.Runs, list.Length, buffer.Memory.Slice(offset, MftClusterValue.BytesFor(source.BytesPerCluster, list.Length)), ct);

    /// <summary>The buffer kept in <paramref name="slot"/>, replaced by a larger one where it cannot hold <paramref name="bytes"/>.</summary>
    private static VolumeReadBuffer BufferFor(VolumeReadBuffer?[] buffers, int slot, int bytes)
    {
        if (buffers[slot] is { } kept && kept.Length >= bytes)
        {
            return kept;
        }

        ((IDisposable?)buffers[slot])?.Dispose();
        buffers[slot] = null;

        return buffers[slot] = new VolumeReadBuffer(bytes);
    }
}
