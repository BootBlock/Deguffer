using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the <c>$ATTRIBUTE_LIST</c>s the first pass found kept outside the table: several at once,
/// and in the order of their clusters, so the disk is kept busy and moves forward through the volume
/// rather than back and forth across it. Lists close together are read together, in reads
/// <see cref="MftListReadPlanner"/> plans.
///
/// <para><b>A read that lands starts the next one</b>, on the thread it landed on, as
/// <see cref="MftReadPass"/> does, so the disk is asked for the reads in flight whichever of them is
/// slow. A read waiting on the calling thread for the oldest to land would let every read behind it
/// finish with nothing to replace it.</para>
///
/// <para><b>Each list is handed on in cluster order</b>, whatever order the reads complete in, so what
/// a table wanting more extension records than it holds loses is the same on every run. A list is
/// read out of its buffer as soon as its read lands, and its entries wait for their turn. Reads are
/// made at most <see cref="RoundsAhead"/> times the reads in flight past the oldest read not yet
/// handed on, so what waits is bounded whatever the table.</para>
///
/// <para><b>A list is handed on as it would be read alone.</b> Where a read of several fails, each of
/// its lists is read again on its own, so an unreadable cluster loses only the list it is part of,
/// and a gap read across never loses a list at all.</para>
///
/// <para><b>Each read in flight has one buffer</b>, kept for the next read made in its place, so a
/// table with thousands of lists costs no more memory than one with a few, and no buffer is made per
/// read.</para>
///
/// <para><b>Nothing is released while a read is in flight.</b> A read lands in its buffer whenever the
/// disk finishes, so every way out, a cancellation or a failure included, first cancels the reads
/// still in flight and waits for them.</para>
/// </summary>
internal sealed class MftAttributeListReader
{
    /// <summary>
    /// How many rounds of the reads in flight may be made past the oldest read whose lists are not yet
    /// handed on. One slow read then holds back no other until the rest have gone round this many times
    /// behind it, and the entries waiting for their turn are never more than this many rounds of reads
    /// hold, which a parsed list is no larger than.
    /// </summary>
    private const int RoundsAhead = 4;

    private readonly IMftSource _source;
    private readonly List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)> _lists;
    private readonly IReadOnlyList<MftListRead> _plan;
    private readonly int _clusterBytes;
    private readonly int _ahead;
    private readonly CancellationTokenSource _cancelReads;

    /// <summary>The buffer each place for a read in flight keeps, touched only by the read made in it.</summary>
    private readonly VolumeReadBuffer?[] _buffers;

    /// <summary>The run of reads made in each place, written only on the calling thread.</summary>
    private readonly Task?[] _workers;

    /// <summary>Each list's entries, from when its read lands until it is handed on.</summary>
    private readonly IReadOnlyList<MftAttributeListEntry>?[] _entries;

    /// <summary>Each read once it has landed, whether it succeeded or not.</summary>
    private readonly Task?[] _landed;

    /// <summary>Places with no read in flight, waiting for a read the window allows.</summary>
    private readonly Stack<int> _idle;

    // Monitor rather than Lock: the calling thread waits on it for the read whose turn it is.
    private readonly object _gate = new();
    private int _next;
    private int _handing;
    private bool _stopping;

    private MftAttributeListReader(
        IMftSource source,
        List<(MftDeferredRecord Record, IReadOnlyList<DataRun> Runs, int Length)> lists,
        int readsInFlight,
        CancellationToken ct)
    {
        _source = source;
        _lists = lists;
        _clusterBytes = source.BytesPerCluster;
        _plan = MftListReadPlanner.Plan(lists.ConvertAll(static l => (l.Runs, l.Length)), _clusterBytes);
        _ahead = readsInFlight * RoundsAhead;
        _buffers = new VolumeReadBuffer?[readsInFlight];
        _workers = new Task?[readsInFlight];
        _entries = new IReadOnlyList<MftAttributeListEntry>?[lists.Count];
        _landed = new Task?[_plan.Count];
        _idle = new Stack<int>(readsInFlight);
        _cancelReads = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

        new MftAttributeListReader(source, lists, readsInFlight, ct).HandOn(onList, ct);
    }

    private void HandOn(Func<MftDeferredRecord, IReadOnlyList<MftAttributeListEntry>?, bool> onList, CancellationToken ct)
    {
        try
        {
            // The calling thread waits for a read, not for the token, and nothing else would wake it.
            using var wake = ct.Register(static state => ((MftAttributeListReader)state!).Wake(), this);

            for (var place = 0; place < _workers.Length; place++)
            {
                Start(place);
            }

            for (var read = 0; read < _plan.Count; read++)
            {
                var landing = Landed(read, ct);

                if (landing.Exception is { } failure)
                {
                    ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
                }

                if (landing.IsCanceled)
                {
                    throw new TaskCanceledException(landing);
                }

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

                lock (_gate)
                {
                    _handing++;
                }

                // A place that stopped at the edge of the window can read again now it has moved.
                while (TryResume(out var place))
                {
                    Start(place);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _stopping = true;
            }

            _cancelReads.Cancel();

            foreach (var worker in _workers)
            {
                // Each read's failure is the calling thread's to throw at its turn, so a run of reads
                // never fails itself. Waited for so no read is still landing in a buffer released below.
                worker?.Wait();
            }

            foreach (var buffer in _buffers)
            {
                ((IDisposable?)buffer)?.Dispose();
            }

            _cancelReads.Dispose();
        }
    }

    /// <summary>Make reads in <paramref name="place"/> until there is none the window allows.</summary>
    private void Start(int place)
    {
        int read;

        lock (_gate)
        {
            if (!TryClaim(out read))
            {
                _idle.Push(place);
                return;
            }
        }

        // Written before this place can be resumed, which only this thread does.
        _workers[place] = WorkAsync(place, read);
    }

    private async Task WorkAsync(int place, int read)
    {
        while (true)
        {
            var landing = LandAsync(place, read);

            // Thrown on the calling thread at the read's turn. Thrown here, on a thread the system
            // completed the read on, nothing would ever see it.
            await landing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // Read so a failure past where the calling thread stopped is not reported later as one
            // nobody saw. One it does reach is thrown there again.
            _ = landing.Exception;

            lock (_gate)
            {
                _landed[read] = landing;

                if (read == _handing)
                {
                    Monitor.Pulse(_gate);
                }

                if (!TryClaim(out read))
                {
                    _idle.Push(place);
                    return;
                }
            }
        }
    }

    /// <summary>Make <paramref name="index"/> into the buffer of <paramref name="place"/>, and read each of its lists out of it.</summary>
    private async Task LandAsync(int place, int index)
    {
        var read = _plan[index];
        var buffer = BufferFor(place, read.Bytes);
        var ct = _cancelReads.Token;
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

    /// <summary>The next read, where the window allows one and nothing is stopping. Called holding the gate.</summary>
    private bool TryClaim(out int read)
    {
        read = _next;

        if (_stopping || _cancelReads.IsCancellationRequested || _next >= _plan.Count || _next >= _handing + _ahead)
        {
            return false;
        }

        _next++;
        return true;
    }

    /// <summary>A place with no read in flight, where the window has room for one. A run of reads already in flight may still claim that room first.</summary>
    private bool TryResume(out int place)
    {
        lock (_gate)
        {
            place = 0;
            return !_stopping && _next < _plan.Count && _next < _handing + _ahead && _idle.TryPop(out place);
        }
    }

    /// <summary>The read whose turn it is, once it has landed.</summary>
    private Task Landed(int read, CancellationToken ct)
    {
        lock (_gate)
        {
            while (_landed[read] is null)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(_gate);
            }

            return _landed[read]!;
        }
    }

    private void Wake()
    {
        lock (_gate)
        {
            Monitor.Pulse(_gate);
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
