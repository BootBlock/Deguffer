using System.Collections.Concurrent;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which threads drove the crews an operation made, through <see cref="WorkerCrew.Made"/>: how a test
/// pins a walk or a pass off the thread pool.
/// </summary>
internal static class CrewThreads
{
    /// <summary>Whether each crew <paramref name="act"/> made was made on a pool thread.</summary>
    public static async Task<IReadOnlyList<bool>> MadeBy(Func<Task> act)
    {
        var made = new ConcurrentQueue<bool>();
        WorkerCrew.Made.Value = made.Enqueue;

        try
        {
            await act();
        }
        finally
        {
            WorkerCrew.Made.Value = null;
        }

        return [.. made];
    }

    /// <summary>Some crew was made, so the operation walked, and none on a pool thread.</summary>
    public static void AssertOffThePool(IReadOnlyList<bool> made)
    {
        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }
}
