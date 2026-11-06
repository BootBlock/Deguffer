using System.Collections.Concurrent;

namespace Deguffer.Testing;

/// <summary>
/// Concurrent work made to complete in a stated order: each piece waits until the one before it in
/// the order has completed. A delay or a sleep only makes an order likely, and a busy machine that
/// holds one thread back for longer than the delay completes the work in another order.
///
/// <para>Only work already started can complete, so the order must let each piece's predecessor
/// start while the piece waits: within the work the code under test allows in flight at once. A
/// piece whose turn never comes fails rather than hanging the test.</para>
/// </summary>
/// <typeparam name="TKey">What names one piece of work. Each piece appears in the order once.</typeparam>
public sealed class CompletionOrder<TKey>
    where TKey : notnull
{
    /// <summary>Far longer than a piece of work takes, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TKey[] _order;
    private readonly Dictionary<TKey, int> _positions;
    private readonly TaskCompletionSource[] _completed;
    private readonly ConcurrentQueue<TKey> _completions = new();

    /// <param name="order">The pieces of work in the order they are to complete.</param>
    public CompletionOrder(IEnumerable<TKey> order)
    {
        _order = [.. order];
        _positions = _order.Index().ToDictionary(entry => entry.Item, entry => entry.Index);
        _completed = [.. _order.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    }

    /// <summary>
    /// What is in the order, in the order it was reported complete, which for work held by
    /// <see cref="TurnOf"/> or run by <see cref="InTurn"/> is the stated order.
    /// </summary>
    public IReadOnlyList<TKey> Completions => [.. _completions];

    /// <summary>Whether <paramref name="key"/> is in the order.</summary>
    public bool Contains(TKey key) => _positions.ContainsKey(key);

    /// <summary>
    /// Completes once the piece before <paramref name="key"/> has completed, at once for the first.
    /// Throws <see cref="TimeoutException"/> where that piece never completes, and
    /// <see cref="KeyNotFoundException"/> for a piece the order does not hold, so work the test did
    /// not expect fails it rather than going unheld.
    /// </summary>
    public async Task TurnOf(TKey key, CancellationToken ct = default)
    {
        var position = _positions[key];
        if (position == 0)
        {
            return;
        }

        try
        {
            await _completed[position - 1].Task.WaitAsync(Patience, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"'{_order[position - 1]}' never completed, so '{key}' after it could not.");
        }
    }

    /// <summary>Record that <paramref name="key"/> completed, which lets the piece after it complete.</summary>
    public void Complete(TKey key)
    {
        var completed = _completed[_positions[key]];

        // Recorded before the next piece is released, so the record is in completion order.
        if (!completed.Task.IsCompleted)
        {
            _completions.Enqueue(key);
            completed.TrySetResult();
        }
    }

    /// <summary>
    /// Run <paramref name="operation"/> in the turn of <paramref name="key"/>. It counts as complete
    /// only once the task returned here has completed, whether it succeeded, failed or was cancelled,
    /// so the next piece never completes before a caller could see this one complete, and a failure
    /// does not hold up the pieces after it.
    /// </summary>
    public Task<T> InTurn<T>(TKey key, Func<Task<T>> operation, CancellationToken ct)
    {
        var run = Run();
        _ = run.ContinueWith(_ => Complete(key), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;

        async Task<T> Run()
        {
            await TurnOf(key, ct).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
    }
}
