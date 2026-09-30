using System.Collections.Concurrent;

namespace Deguffer.Testing;

/// <summary>
/// Every exception thrown while a piece of code ran, caught ones included, for a test whose claim is
/// that an ordinary outcome is reported rather than thrown.
///
/// <para>A caught exception leaves nothing a result can show, so the only witness is the runtime's
/// first-chance notification. That notification is process-wide and the suite runs in parallel, so
/// what is recorded is scoped by an <see cref="AsyncLocal{T}"/>: it flows into the tasks and
/// parallel loops the code starts, and into nothing another test started.</para>
/// </summary>
public static class ThrownExceptions
{
    private static readonly AsyncLocal<ConcurrentQueue<Exception>?> Recording = new();

    static ThrownExceptions() =>
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => Recording.Value?.Enqueue(e.Exception);

    /// <summary>Run <paramref name="action"/> and return what it threw.</summary>
    public static IReadOnlyList<Exception> During(Action action)
    {
        var seen = new ConcurrentQueue<Exception>();
        Recording.Value = seen;

        try
        {
            action();
        }
        finally
        {
            // A synchronous method's change to an AsyncLocal outlives it, unlike an async one's.
            Recording.Value = null;
        }

        return [.. seen];
    }

    /// <summary>Run <paramref name="action"/> to completion and return what it threw.</summary>
    public static async Task<IReadOnlyList<Exception>> DuringAsync(Func<Task> action)
    {
        var seen = new ConcurrentQueue<Exception>();
        Recording.Value = seen;

        await action();

        return [.. seen];
    }
}
