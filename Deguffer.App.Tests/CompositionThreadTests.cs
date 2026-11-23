namespace Deguffer.App.Tests;

/// <summary>
/// The compositor's thread every composition test hands its work to, held to what a test waiting on a
/// commit needs of it: to hear the commit, and to be handed back somewhere it can carry on.
/// </summary>
public class CompositionThreadTests
{
    /// <summary>
    /// A test that has already resumed once has no context to return to, so a commit heard inside the
    /// compositor's callback carried it on there, and its next piece of work for the compositor waited
    /// on the thread it was itself holding, which hung the whole run.
    ///
    /// <para>Each commit is awaited from a fresh thread-pool task with no context, and checked before
    /// anything else is handed to the compositor, so a test carried on inside the callback fails here
    /// rather than hanging.</para>
    /// </summary>
    [Fact]
    public async Task ACommitIsHeardOffTheCompositorsThreadByATestWithNoContextToReturnTo()
    {
        var compositorThread = CompositionThread.Run(() => Environment.CurrentManagedThreadId);

        for (var commit = 0; commit < 20; commit++)
        {
            var resumedOn = await Task.Run(async () =>
            {
                Assert.Null(SynchronizationContext.Current);
                await CompositionThread.CommitAsync();

                return Environment.CurrentManagedThreadId;
            });

            Assert.NotEqual(compositorThread, resumedOn);
        }
    }

    /// <summary>
    /// Work handed to the compositor from its own thread is queued behind the work doing the handing, and
    /// waiting for it there waits forever. It is refused instead, so the test that did it fails.
    /// </summary>
    [Fact]
    public void WorkHandedToTheCompositorFromItsOwnThreadIsRefusedRatherThanWaitedOn()
    {
        var refused = CompositionThread.Run(() => Record.Exception(() => CompositionThread.Run(() => 0)));

        Assert.IsType<InvalidOperationException>(refused);
    }
}
