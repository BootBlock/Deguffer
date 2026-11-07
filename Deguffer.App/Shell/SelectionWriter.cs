using System.Runtime.ExceptionServices;
using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// Writes the remembered ticks on a worker, one write at a time, always the newest.
///
/// <para>The file names every step of every row the user has changed, so each write serialises all
/// of it. Written on the window's thread, a large source-tree row cost milliseconds of every tick.
/// Written here, a tick costs a shallow copy.</para>
///
/// <para>A write still starts at every change, with no timer and no delay, for the reason the
/// Storage page gives for writing at every change at all: there is no reliable later moment. Changes
/// that arrive while a write is under way replace one another, and the newest is written as soon as
/// that write ends, so a burst of ticks during a slow write costs two writes rather than one each.
/// <see cref="Flush"/> covers the moments the process may end before the worker gets there.</para>
/// </summary>
internal sealed class SelectionWriter(ISelectionStore store)
{
    private readonly Lock _gate = new();

    /// <summary>The newest copy not yet taken by a write. Under <see cref="_gate"/>.</summary>
    private IReadOnlyDictionary<string, RememberedSelection>? _pending;

    /// <summary>
    /// The worker writing now, or the last one to finish, which ends only once nothing is pending.
    /// Under <see cref="_gate"/>.
    /// </summary>
    private Task _writing = Task.CompletedTask;

    private bool _isWriting;

    /// <summary>
    /// Write <paramref name="snapshot"/> once the write under way ends, or now where none is, and
    /// return without waiting. The caller hands over a copy nothing else changes.
    /// </summary>
    public void Write(IReadOnlyDictionary<string, RememberedSelection> snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;

            if (_isWriting)
            {
                // The worker takes it when its write ends: it checks under this lock before it stops.
                return;
            }

            _isWriting = true;
            _writing = Task.Run(WriteWhilePending);
        }
    }

    /// <summary>
    /// Wait until every copy handed over has been written. Called as the window closes and before the
    /// elevated relaunch starts the replacement, which reads the file as it opens.
    /// </summary>
    public void Flush()
    {
        Task writing;

        lock (_gate)
        {
            writing = _writing;
        }

        writing.GetAwaiter().GetResult();
    }

    private void WriteWhilePending()
    {
        ExceptionDispatchInfo? fault = null;

        while (TakePending() is { } next)
        {
            try
            {
                // A write that fails costs the choice at the next launch and nothing sooner, as
                // SelectionService.Remember explains, so its answer goes nowhere.
                store.Save(next);
            }
            catch (Exception e)
            {
                // Only a fault the store does not expect reaches here. The worker goes on to the
                // change that arrived during the faulted write, which nothing else would start a
                // write for, and fails its task with the first fault once nothing is pending, for the
                // flush to report.
                fault ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        fault?.Throw();
    }

    /// <summary>The newest copy, taken, or null once there is none, which ends the worker.</summary>
    private IReadOnlyDictionary<string, RememberedSelection>? TakePending()
    {
        lock (_gate)
        {
            var next = _pending;
            _pending = null;
            _isWriting = next is not null;

            return next;
        }
    }
}
