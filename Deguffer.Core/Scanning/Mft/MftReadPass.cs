using System.Collections.Concurrent;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The first pass over a table: reads records into a fixed set of buffers with several reads
/// outstanding, and parses each buffer as it arrives on up to <see cref="TableTuning.ParseThreads"/>
/// threads, so the drive is not idle while records are parsed and the processor is not idle while
/// they are read. What is done with each record is <see cref="MftRecordHandOff"/>'s.
///
/// <para><b>Every rule of a single-threaded pass still holds</b>, but one. A record is numbered by its position
/// in the table, which is where its read started plus where it lies in the buffer, never by the order
/// reads complete in. A short read is never skipped past: the rest of that read is read again into
/// the same buffer, and a read that returns nothing is a region that could not be read. From that
/// region on nothing more is read and nothing more is handed on, though a record after it may already
/// have been. Every record before it is handed on. The one rule that changes is where a table that
/// wants more extension records than it holds is stopped: at the first record found to push the
/// wants past the table, which with several parse threads need not be the first in table order.
/// Such a table is not one NTFS wrote, and it is reported as not read whole either way.</para>
///
/// <para><b>Nothing is released while a read is in flight.</b> A read lands in its buffer whenever the
/// disk finishes, so every way out of <see cref="Run"/>, a cancellation or a failure included, first
/// waits for every read to complete and every parse to finish. Stopping cancels the reads still in
/// flight, so the wait is short.</para>
///
/// <para><b>The calling thread parses too, and is the only one that waits.</b> The other parse
/// threads are a <see cref="WorkerCrew"/>'s helpers, started as completed reads arrive. A read
/// completes on whatever thread the system completes it on, and only queues its buffer and starts
/// more reads.</para>
///
/// <para><b>The calling thread pumps before every look.</b> Any thread may start reads, and in
/// doing so may find the table planned to its end, stop the pass at a region it cannot read, or give
/// back the buffers of reads it will not make again. A pump that is never turned away, and does
/// each of those under one lock, lets the calling thread find all of them for itself. What it has to
/// be told of is only what happens elsewhere: a read arriving, a parse thread ending, a failure or a
/// cancellation, and each of those wakes it.</para>
/// </summary>
internal sealed class MftReadPass : IDisposable
{
    private readonly IMftSource _source;
    private readonly int _readsAllowed;
    private readonly int _bytesPerRecord;
    private readonly CancellationToken _ct;
    private readonly MftBatchPlanner _planner;
    private readonly MftRecordHandOff _records;
    private readonly MftPassProgress _progress;
    private readonly WorkerCrew _crew;
    private readonly Func<bool> _readyToLook;

    private readonly VolumeReadBuffer[] _buffers;
    private readonly Stack<VolumeReadBuffer> _free;
    private readonly Queue<Slot> _again = new();
    private readonly ConcurrentQueue<Slot> _arrived = new();
    private readonly CancellationTokenSource _cancelReads;
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private int _readsInFlight;
    private int _busy;
    private long _skippedAtEnd;

    // Only ever set, so read and written without the lock.
    private bool _planned;

    private long _stopAt = long.MaxValue;
    private bool _abandoned;

    public MftReadPass(
        IMftSource source,
        int count,
        TableTuning tuning,
        MftBitmap? bitmap,
        MftRecordHandler onRecord,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        _source = source;
        _bytesPerRecord = source.BytesPerRecord;
        _readsAllowed = tuning.ReadsInFlight;
        _ct = ct;
        _cancelReads = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _records = new MftRecordHandOff(count, bitmap, onRecord);
        _progress = new MftPassProgress(onProgress, () => Stopping);
        _crew = new WorkerCrew(tuning.ParseThreads, () => ParseArrived, () => !_arrived.IsEmpty);
        _readyToLook = () => !_arrived.IsEmpty || IsOver();

        // At least one record, where a record is larger than a read: a read is never of part of one.
        // At most the whole table, because every buffer is held for the whole pass, and a small
        // table read with the largest values would otherwise hold far more than it is.
        var capacity = Math.Max(1, Math.Min(tuning.RecordsPerRead(_bytesPerRecord), count));
        _planner = new MftBatchPlanner(source, count, capacity, bitmap);

        _buffers = new VolumeReadBuffer[tuning.ReadsInFlight + tuning.ParseThreads];
        _free = new Stack<VolumeReadBuffer>(_buffers.Length);

        try
        {
            for (var i = 0; i < _buffers.Length; i++)
            {
                _buffers[i] = new VolumeReadBuffer(capacity * _bytesPerRecord);
                _free.Push(_buffers[i]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private bool Stopping => _crew.Failed || Volatile.Read(ref _abandoned) || _ct.IsCancellationRequested;

    /// <summary>
    /// A buffer the pass has finished with, for the second pass to read extension records into.
    /// Meaningful only once <see cref="Run"/> has returned.
    /// </summary>
    public Span<byte> SpareBuffer => _buffers[0].Span;

    /// <summary>
    /// Read and parse every record up to the count. Throws what a read or the handler threw, or
    /// <see cref="OperationCanceledException"/>, and only once nothing is in flight.
    /// </summary>
    public MftFirstPass Run()
    {
        using var wake = _crew.WakeOn(_ct);

        _progress.Begin();

        Pump();
        WorkAndWait();

        _crew.Finish();
        _ct.ThrowIfCancellationRequested();

        // The free records after the last read are found only when the plan ends, on whichever thread
        // found it, and with no buffer lent to keep the pass from ending first. Counted here, on the
        // calling thread, so no report comes after this returns.
        _progress.Advance(_skippedAtEnd);

        return new MftFirstPass(
            WholeTable: Interlocked.Read(ref _stopAt) == long.MaxValue,
            Abandoned: _abandoned,
            _records.Deferred(),
            _records.Wanted);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            ((IDisposable?)buffer)?.Dispose();
        }

        _cancelReads.Dispose();
    }

    private void WorkAndWait()
    {
        while (true)
        {
            ParseArrived();

            // A cancellation wakes this thread and nothing else, and a read waiting to be made
            // again holds its buffer until something looks at it.
            Pump();

            if (IsOver())
            {
                return;
            }

            _crew.WaitUnless(_readyToLook);
        }
    }

    private void ParseArrived()
    {
        while (_arrived.TryDequeue(out var slot))
        {
            Parse(slot);
        }
    }

    /// <summary>
    /// Whether every buffer is back and none will be lent again: the table is planned to its end, or
    /// the pass is stopping.
    /// </summary>
    private bool IsOver()
    {
        lock (_gate)
        {
            return _busy == 0 && (Volatile.Read(ref _planned) || Stopping);
        }
    }

    /// <summary>
    /// Start reads while a buffer and a place in flight are both free. Safe to call from any thread
    /// at once, and from inside itself, where a read completes at once: each read takes a buffer, so
    /// that goes no deeper than there are buffers.
    /// </summary>
    private void Pump()
    {
        while (true)
        {
            Slot? slot;
            long skipped;

            lock (_gate)
            {
                slot = TakeNextRead(out skipped);
                if (slot is null)
                {
                    return;
                }

                _readsInFlight++;
            }

            // Reported while the slot is lent, which keeps the pass from ending before the report.
            try
            {
                _progress.Advance(skipped);
            }
            catch (Exception ex)
            {
                // The progress callback is the caller's, and this may be a thread a read completed
                // on, where a throw would leave the read it just made half accounted for.
                Fail(ex);
            }

            Issue(slot);
        }
    }

    /// <summary>The next read to start, or null. Called under <see cref="_gate"/>.</summary>
    private Slot? TakeNextRead(out long skipped)
    {
        skipped = 0;

        if (Stopping)
        {
            while (_again.TryDequeue(out var abandoned))
            {
                ReturnBuffer(abandoned.Buffer);
            }

            return null;
        }

        if (_readsInFlight >= _readsAllowed)
        {
            return null;
        }

        // The rest of a short read before anything new, so the table is read in order as far as it
        // can be.
        while (_again.TryDequeue(out var again))
        {
            again.ReadCount = again.Position < Interlocked.Read(ref _stopAt)
                ? _source.BatchLength(again.Position, (int)(again.End - again.Position))
                : 0;

            if (again.ReadCount > 0)
            {
                return again;
            }

            StopAt(again.Position);

            ReturnBuffer(again.Buffer);
        }

        if (Volatile.Read(ref _planned) || _free.Count == 0)
        {
            return null;
        }

        if (!_planner.TryNext(out var batch))
        {
            Volatile.Write(ref _planned, true);
            _skippedAtEnd += batch.Skipped;
            return null;
        }

        if (batch.Count == 0)
        {
            StopAt(batch.First);
            _skippedAtEnd += batch.Skipped;
            return null;
        }

        skipped = batch.Skipped;

        _busy++;
        return new Slot(_free.Pop(), batch.First, batch.End) { ReadCount = batch.Count };
    }

    private void Issue(Slot slot)
    {
        ValueTask<int> read;

        try
        {
            read = _source.ReadBatchAsync(
                slot.Position, slot.Buffer.Memory[..(slot.ReadCount * _bytesPerRecord)], _cancelReads.Token);
        }
        catch (Exception ex)
        {
            // Thrown before the read was in flight, so nothing will land in the buffer.
            Arrive(slot, 0, ex);
            return;
        }

        if (read.IsCompletedSuccessfully)
        {
            Arrive(slot, read.Result, null);
            return;
        }

        _ = AwaitRead(slot, read);
    }

    private async Task AwaitRead(Slot slot, ValueTask<int> read)
    {
        int records;
        Exception? failure = null;

        try
        {
            records = await read.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Held for the calling thread, which throws it once nothing is in flight. Thrown here, on
            // a thread the system completed the read on, nothing would ever see it.
            records = 0;
            failure = ex;
        }

        // Outside the catch, so the read arrives once whatever happens after it.
        Arrive(slot, records, failure);
    }

    private void Arrive(Slot slot, int records, Exception? failure)
    {
        slot.Records = records;
        slot.Failure = failure;

        lock (_gate)
        {
            _readsInFlight--;
        }

        _arrived.Enqueue(slot);

        // The calling thread may be waiting for this read, and nothing else would tell it.
        _crew.Signal();
        _crew.TryStartHelper();
        Pump();
    }

    /// <summary>
    /// Hand on the records a read brought, and then either read the rest of a short read into the
    /// same buffer or give the buffer back. Never throws: whatever goes wrong stops the pass.
    /// </summary>
    private void Parse(Slot slot)
    {
        var readAgain = false;

        try
        {
            if (slot.Failure is { } failure)
            {
                // Only a read the pass still wants fails it. One made before the pass stopped, of
                // records past where it stopped, is a read a pass one at a time would never have
                // made, and one cancelled because the pass is stopping is not a second failure.
                if (!Stopping && slot.Position < Interlocked.Read(ref _stopAt))
                {
                    Fail(failure);
                }
            }
            else if (!Stopping)
            {
                if (slot.Records <= 0)
                {
                    StopAt(slot.Position);
                }
                else
                {
                    var records = Math.Min(slot.Records, slot.ReadCount);

                    HandOn(slot.Buffer.Span, slot.Position, records);
                    _progress.Advance(records);

                    slot.Position += records;
                    readAgain = slot.Position < slot.End;
                }
            }
        }
        catch (Exception ex)
        {
            // The handler is the caller's, and what it throws is the caller's to see.
            Fail(ex);
        }

        lock (_gate)
        {
            if (readAgain && !Stopping)
            {
                _again.Enqueue(slot);
            }
            else
            {
                ReturnBuffer(slot.Buffer);
            }
        }

        Pump();
    }

    private void HandOn(Span<byte> records, long first, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var number = first + i;

            if (number >= Interlocked.Read(ref _stopAt) || Stopping)
            {
                return;
            }

            switch (_records.Take(number, records.Slice(i * _bytesPerRecord, _bytesPerRecord)))
            {
                case MftHandOn.WantsTooMuch:
                    StopAt(number);
                    return;

                case MftHandOn.Abandoned:
                    Volatile.Write(ref _abandoned, true);
                    _cancelReads.Cancel();
                    return;
            }
        }
    }

    /// <summary>
    /// End the pass at <paramref name="record"/>: nothing from there on is read or handed on, and
    /// the table was not read whole. Reads already in flight before it still land and are handed on.
    /// </summary>
    private void StopAt(long record)
    {
        var current = Interlocked.Read(ref _stopAt);

        while (record < current)
        {
            var seen = Interlocked.CompareExchange(ref _stopAt, record, current);
            if (seen == current)
            {
                break;
            }

            current = seen;
        }

        Volatile.Write(ref _planned, true);
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void ReturnBuffer(VolumeReadBuffer buffer)
    {
        _free.Push(buffer);
        _busy--;
    }

    private void Fail(Exception ex)
    {
        _crew.Fail(ex);
        _cancelReads.Cancel();
    }

    /// <summary>
    /// One buffer and the read it is lent to: records <see cref="Position"/> onward, up to
    /// <see cref="End"/>, which is where the read it was planned for ends.
    /// </summary>
    private sealed class Slot(VolumeReadBuffer buffer, long first, long end)
    {
        public VolumeReadBuffer Buffer { get; } = buffer;

        public long End { get; } = end;

        public long Position { get; set; } = first;

        /// <summary>How many records the read in flight asked for.</summary>
        public int ReadCount { get; set; }

        /// <summary>How many records the read brought. Zero where it brought none.</summary>
        public int Records { get; set; }

        public Exception? Failure { get; set; }
    }
}
