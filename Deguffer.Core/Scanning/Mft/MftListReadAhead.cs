using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the <c>$ATTRIBUTE_LIST</c>s kept outside the table while the first pass is still reading
/// the table (<see cref="AttributeListReads.BesideFirstPass"/>), so their small scattered reads share
/// the disk with the pass's large ones rather than waiting for the last of them.
///
/// <para><b>A list's place is known once its record is parsed</b> (<see cref="MftDeferredRecord.PendingList"/>),
/// so the pass hands each such record over as it holds it back. They are read in batches, each in
/// the order of its clusters and planned by <see cref="MftListReadPlanner"/> through
/// <see cref="MftAttributeListReader"/>. A batch is at least <see cref="TableTuning.ListBatch"/>
/// lists, or whatever is left once the pass ends.</para>
///
/// <para><b>Nothing is followed here.</b> Each list is only landed (<see cref="MftDeferredRecord.Land"/>):
/// what it wants is worked out and kept. What the table's budget allows depends on what the whole
/// pass wanted, and is spent in cluster order over every list once the pass has ended
/// (<see cref="MftExtensionReader"/>), so a table wanting more extension records than it holds loses
/// the same records whichever batch each list was read in, and whichever order the reads completed
/// in.</para>
///
/// <para><b>What is landed is bounded.</b> A table wants no more extension records than it holds,
/// so lists landed here wanting more than that are a table NTFS did not write. Reading stops at once,
/// and the second pass reads the lists as it does after a pass read without this, spending the budget
/// as it reads. Holding every want of a hostile table would grow without bound.</para>
///
/// <para><b>One buffer per read in flight serves every batch</b>, as it serves every read of one
/// batch, and they are released once the last batch is read, before the lists are followed or, where
/// reading stopped, read again with buffers of their own. So reading beside the pass never holds
/// more buffers than reading after it.</para>
///
/// <para><b>Nothing is released while a read is in flight.</b> Every way out, a failure of the pass or
/// of a list read, or a cancellation, cancels the reads and waits for the thread reading them.</para>
/// </summary>
internal sealed class MftListReadAhead : IDisposable
{
    private readonly IMftSource _source;
    private readonly int _batch;
    private readonly VolumeReadBuffer?[] _buffers;
    private readonly long _most;
    private readonly CancellationTokenSource _cancel;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Lock _gate = new();
    private readonly Task<bool> _reading;

    // Guarded by _gate. Swapped with the batch being read, so neither list is made again.
    private List<MftDeferredRecord> _waiting = [];
    private bool _passEnded;

    // Only ever set, and only by the reading thread. Read by the parse threads to stop handing over.
    private bool _stopped;

    /// <param name="readsInFlight">How many reads of lists are outstanding at once, at most.</param>
    /// <param name="batch">How many lists a batch waits for, as <see cref="TableTuning.ListBatch"/>.</param>
    /// <param name="most">How many extension records the table holds, which the lists landed here can never want more of.</param>
    public MftListReadAhead(IMftSource source, int readsInFlight, int batch, long most, CancellationToken ct)
    {
        _source = source;
        _batch = batch;
        _buffers = new VolumeReadBuffer?[readsInFlight];
        _most = most;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // A thread of its own for the pass, since a batch blocks on its reads and the pass is seconds.
        _reading = WorkerThreads.Shared.RunAsync(Read, _cancel.Token);
    }

    /// <summary>
    /// Read the list of <paramref name="record"/>, which the pass has just held back and whose list is
    /// kept outside the table. Called from every parse thread at once.
    /// </summary>
    public void Add(MftDeferredRecord record)
    {
        if (Volatile.Read(ref _stopped))
        {
            return;
        }

        lock (_gate)
        {
            _waiting.Add(record);

            if (_waiting.Count >= _batch)
            {
                _ready.Set();
            }
        }
    }

    /// <summary>
    /// Read whatever the pass handed over and has not been read yet, and wait for every read. Returns
    /// whether every list handed over was landed: false where the lists outgrew the table and reading
    /// stopped. Throws what a read threw.
    /// </summary>
    public bool Finish()
    {
        lock (_gate)
        {
            _passEnded = true;
            _ready.Set();
        }

        // The reading is synchronous work on a thread of its own, so this waits for that thread, and
        // throws what it failed with here, on the thread that will follow the lists.
        Task.WaitAny(_reading);
        MftAttributeListReader.Release(_buffers);

        if (_reading.Exception is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
        }

        if (_reading.IsCanceled)
        {
            throw new TaskCanceledException(_reading);
        }

        return _reading.Result;
    }

    /// <summary>
    /// Stop reading, for a pass that will not be followed, and wait until nothing is in flight. What a
    /// read threw is left unthrown: whatever ended the pass already says why.
    /// </summary>
    public void Dispose()
    {
        _cancel.Cancel();

        // Waited for, never thrown: see the summary. Its exception is read so it is not reported later
        // as one nobody saw.
        Task.WaitAny(_reading);
        _ = _reading.Exception;

        MftAttributeListReader.Release(_buffers);
        _cancel.Dispose();
        _ready.Dispose();
    }

    private bool Read()
    {
        var batch = new List<MftDeferredRecord>();
        long wanted = 0;

        while (true)
        {
            _ready.Wait(_cancel.Token);

            bool last;

            lock (_gate)
            {
                (batch, _waiting) = (_waiting, batch);
                last = _passEnded;

                // Set again by the next record to fill a batch, or kept set once the pass has ended,
                // so a batch is never waited for that will not come.
                if (!last)
                {
                    _ready.Reset();
                }
            }

            var landedAll = true;

            MftAttributeListReader.Read(_source, batch, _buffers, (record, entries) =>
            {
                wanted += record.Land(entries);
                landedAll = wanted <= _most;
                return landedAll;
            }, _cancel.Token);

            batch.Clear();

            if (!landedAll)
            {
                Volatile.Write(ref _stopped, true);
                return false;
            }

            if (last)
            {
                return true;
            }
        }
    }
}
