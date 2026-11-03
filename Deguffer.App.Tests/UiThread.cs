using System.Collections.Concurrent;

namespace Deguffer.App.Tests;

/// <summary>
/// Runs a test the way the window runs a view-model: on one thread, with every continuation and every
/// <see cref="Progress{T}"/> callback posted back to that thread in order.
///
/// <para>Without it a test sees an order the app never produces. The view-models report through
/// <see cref="Progress{T}"/>, which captures the context it was made on, and a test thread has none,
/// so each callback runs on the thread pool, overlapping the next one and the code after the await.
/// A defect in how a row is built inside such a callback is the kind this project exists to catch,
/// and it cannot be observed from callbacks that race each other.</para>
/// </summary>
public static class UiThread
{
    /// <summary>Run <paramref name="body"/> to completion on this thread, and rethrow whatever it threw.</summary>
    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        using var context = new SingleThreadContext();
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Pump();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Hold this thread, without running anything posted to it, until something else posts to it, as
    /// an answer finished on the thread pool does when the code awaiting it was on this thread. For a
    /// test that has to act while such an answer is finished and still waiting to be told.
    /// </summary>
    /// <returns>False if nothing was posted within <paramref name="timeout"/>.</returns>
    public static bool WaitForPost(TimeSpan timeout) =>
        SynchronizationContext.Current is SingleThreadContext context
            ? context.WaitForPost(timeout)
            : throw new InvalidOperationException("Only a body running inside UiThread.Run has a thread to post to.");

    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Lock _gate = new();

        // Set by each post, and reset under the gate when a wait starts, so a wait is ended only by a
        // post made after it began.
        private readonly ManualResetEventSlim _posted = new();
        private bool _closed;

        /// <summary>
        /// A post that arrives once the test has finished is not run, because the thread it belongs to
        /// has moved on. It comes from work the test left behind, typically after an assertion failed
        /// part-way, and throwing here would throw on a thread-pool thread and take the whole test
        /// host down with it, losing every result rather than reporting the one failure.
        /// </summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    _queue.Add((d, state));
                    _posted.Set();
                }
            }
        }

        public bool WaitForPost(TimeSpan timeout)
        {
            lock (_gate)
            {
                _posted.Reset();
            }

            return _posted.Wait(timeout);
        }

        /// <summary>
        /// Refused rather than run inline: nothing the view-models do sends, and running a send on the
        /// calling thread would be the thread-pool order this type exists to rule out.
        /// </summary>
        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("A view-model under test sent to the UI thread rather than posting.");

        public void Complete()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.CompleteAdding();
            }
        }

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.Dispose();
                _posted.Dispose();
            }
        }
    }
}
