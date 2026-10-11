namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the <c>$ATTRIBUTE_LIST</c>s the first pass found kept outside the table: several at once,
/// and in the order of their clusters, so the disk is kept busy and moves forward through the volume
/// rather than back and forth across it.
///
/// <para><b>Each list is handed on in that order</b>, whatever order the reads complete in, so what a
/// table wanting more extension records than it holds loses is the same on every run.</para>
///
/// <para><b>At most one list per read in flight is held</b>, each in a buffer kept for the next list
/// read into its place, so a table with thousands of lists costs no more memory than one with a
/// few, and no buffer is made per list.</para>
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

        var buffers = new VolumeReadBuffer?[readsInFlight];
        var reads = new Queue<(MftDeferredRecord Record, int Length, VolumeReadBuffer Buffer, Task<bool> Read)>(readsInFlight);
        using var cancelReads = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var next = 0;

            while (next < lists.Count || reads.Count > 0)
            {
                // List k is read into buffer k modulo the reads in flight, which list k minus that
                // many left once its read completed and it was handed on.
                while (next < lists.Count && reads.Count < readsInFlight)
                {
                    ct.ThrowIfCancellationRequested();

                    var (record, runs, length) = lists[next];
                    var bytes = MftClusterValue.BytesFor(source.BytesPerCluster, length);
                    var buffer = BufferFor(buffers, next % readsInFlight, bytes);

                    reads.Enqueue((record, length, buffer, MftClusterValue.TryReadAsync(source, runs, length, buffer.Memory[..bytes], cancelReads.Token)));
                    next++;
                }

                var read = reads.Dequeue();
                var entries = read.Read.GetAwaiter().GetResult()
                    ? MftAttributeList.TryReadEntries(read.Buffer.Span[..read.Length])
                    : null;

                if (!onList(read.Record, entries))
                {
                    return;
                }
            }
        }
        finally
        {
            cancelReads.Cancel();

            foreach (var (_, _, _, pending) in reads)
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
