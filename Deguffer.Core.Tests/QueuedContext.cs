using System.Collections.Concurrent;

namespace Deguffer.Core.Tests;

/// <summary>
/// A stand-in for the UI thread: what is posted to it waits until the test runs it, on the test's
/// own thread, in the order it was posted.
///
/// <para>So a test decides when the owner's thread catches up with the workers, which is the whole
/// of what a redraw's guarantees are about: what a superseded redraw does with work it posted before
/// it was superseded, and what the screen shows between two hand-overs.</para>
/// </summary>
internal sealed class QueuedContext : SynchronizationContext
{
    /// <summary>Long enough for a busy machine, short enough that a hang fails the test rather than the run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posts = [];

    public override void Post(SendOrPostCallback d, object? state) => _posts.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("A redraw never waits on its owner's thread.");

    /// <summary>Run what is posted, waiting for more, until <paramref name="done"/> holds.</summary>
    public void RunUntil(Func<bool> done, string waitingFor)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!done())
        {
            var left = deadline - DateTime.UtcNow;

            if (left <= TimeSpan.Zero || !_posts.TryTake(out var post, left))
            {
                throw new TimeoutException($"Still waiting for {waitingFor}.");
            }

            post.Callback(post.State);
        }
    }

    /// <summary>Run everything posted so far, and whatever that posts in turn.</summary>
    public void RunPending()
    {
        while (_posts.TryTake(out var post))
        {
            post.Callback(post.State);
        }
    }
}
