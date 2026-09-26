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

    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        /// <summary>
        /// Refused rather than run inline: nothing the view-models do sends, and running a send on the
        /// calling thread would be the thread-pool order this type exists to rule out.
        /// </summary>
        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("A view-model under test sent to the UI thread rather than posting.");

        public void Complete() => _queue.CompleteAdding();

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose() => _queue.Dispose();
    }
}
