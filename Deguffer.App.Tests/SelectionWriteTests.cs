using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.Tests;

/// <summary>
/// A tick on the Storage page records the choice on the window's thread and writes the whole file on
/// a worker. Nothing may come of that but the tick costing less: every choice still reaches disk, the
/// newest last, and the close and the elevated relaunch wait until it has.
/// </summary>
public class SelectionWriteTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static string StepOf(int tick) => $@"C:\Users\testuser\cache\{tick}";

    /// <summary>
    /// Record tick number <paramref name="tick"/> as a row whose one step is named for it, so a write
    /// shows which tick it holds. On a worker, so a tick that waited for its write fails here rather
    /// than hanging the run.
    /// </summary>
    private static Task RememberAsync(SelectionService service, int tick) =>
        Task.Run(() => service.Remember(
                "cache",
                new RememberedSelection(IsSelected: true, new Dictionary<string, bool> { [StepOf(tick)] = true })))
            .WaitAsync(Bound);

    private static Task FlushAsync(SelectionService service) => Task.Run(service.Flush).WaitAsync(Bound);

    /// <summary>Which tick the last write to finish holds.</summary>
    private static string WrittenStep(HeldSelectionWrites store)
    {
        Assert.NotNull(store.Written);

        return Assert.Single(store.Written["cache"].Steps.Keys);
    }

    private static async Task<SelectionService> ReadAsync(HeldSelectionWrites store)
    {
        var service = new SelectionService(store);
        await service.Memory.WaitAsync(Bound);

        return service;
    }

    [Fact]
    public async Task ATickReturnsBeforeItsWriteEnds()
    {
        using var store = new HeldSelectionWrites();
        var service = await ReadAsync(store);

        await RememberAsync(service, 1);

        await store.WriteStartedAsync();
        Assert.Null(store.Written);

        store.Release();
        await FlushAsync(service);
        Assert.Equal(StepOf(1), WrittenStep(store));
    }

    /// <summary>
    /// A burst of ticks while one write is held costs one more write, not one each, and the write that
    /// lands last holds the last choice rather than any of the ones before it.
    /// </summary>
    [Fact]
    public async Task TicksDuringOneWriteCostOneMoreWriteThatHoldsTheLastChoice()
    {
        using var store = new HeldSelectionWrites();
        var service = await ReadAsync(store);

        await RememberAsync(service, 1);
        await store.WriteStartedAsync();

        for (var tick = 2; tick <= 10; tick++)
        {
            await RememberAsync(service, tick);
        }

        store.Release();
        await FlushAsync(service);

        Assert.Equal(2, store.Writes);
        Assert.Equal(StepOf(10), WrittenStep(store));
    }

    /// <summary>
    /// The close and the elevated relaunch call this before the process may end, and the replacement
    /// reads the file as it opens, so it must not return while the last choice is still on its way.
    /// </summary>
    [Fact]
    public async Task AFlushReturnsOnlyOnceTheLastChoiceIsOnDisk()
    {
        using var store = new HeldSelectionWrites();
        var service = await ReadAsync(store);

        await RememberAsync(service, 1);
        await store.WriteStartedAsync();
        await RememberAsync(service, 2);

        var flush = Task.Run(service.Flush);
        await Task.WhenAny(flush, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.False(flush.IsCompleted);

        store.Release();
        await flush.WaitAsync(Bound);

        Assert.Equal(StepOf(2), WrittenStep(store));
    }

    /// <summary>
    /// A fault the store does not expect is reported by the flush that follows it, and does not leave
    /// every later tick waiting on a writer that has stopped.
    /// </summary>
    [Fact]
    public async Task AWriteThatFaultsLeavesTheNextChoiceWritten()
    {
        using var store = new HeldSelectionWrites { FaultFirstWrite = true };
        var service = await ReadAsync(store);
        store.Release();

        await RememberAsync(service, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FlushAsync(service));

        await RememberAsync(service, 2);
        await FlushAsync(service);

        Assert.Equal(2, store.Writes);
        Assert.Equal(StepOf(2), WrittenStep(store));
    }
}
