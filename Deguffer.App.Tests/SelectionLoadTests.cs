using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The remembered ticks are read on a worker from launch rather than on the window's thread before its
/// first frame. Nothing may come of that but the frame arriving sooner: the window does not wait for the
/// read, and a scan begun before it finishes still builds every row as the memory says.
/// </summary>
public class SelectionLoadTests
{
    [Fact]
    public async Task TheServiceIsBuiltWithoutWaitingForTheRead()
    {
        using var temp = new TempDirectory();
        var stored = new SelectionStore(new FakeUserEnvironment(temp.Path));
        Assert.True(stored.Save(new Dictionary<string, RememberedSelection>
        {
            ["cache"] = new(IsSelected: false, new Dictionary<string, bool>()),
        }));
        using var held = new HeldSelectionStore(stored);

        // On a worker, so a constructor that waited for the read fails here rather than hanging the run.
        var service = await Task.Run(() => new SelectionService(held)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(service.Memory.IsCompleted);

        held.Release();

        var memory = await service.Memory.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(memory.Snapshot()["cache"].IsSelected);
    }

    /// <summary>
    /// The row is one §3 pre-selects, and the memory says the user left it unticked. Built from a memory
    /// not yet read, it would start ticked, and the next Clean would take what the user had left alone.
    /// </summary>
    [Fact]
    public void AScanBegunBeforeTheReadFinishesStartsEachRowAsTheMemorySays()
    {
        HeldSelectionStore? held = null;
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache], selectionStore: environment =>
        {
            var stored = new SelectionStore(environment);
            Assert.True(stored.Save(new Dictionary<string, RememberedSelection>
            {
                ["cache"] = new(IsSelected: false, new Dictionary<string, bool>()),
            }));

            return held = new HeldSelectionStore(stored);
        });
        using var release = held!;
        cache.Steps = [page.Cache("a", 1024)];

        UiThread.Run(async () =>
        {
            var scan = page.ViewModel.PreviewCommand.ExecuteAsync(null);

            Assert.False(scan.IsCompleted);
            Assert.False(page.Selections.Memory.IsCompleted);

            release.Release();
            await scan;
        });

        var row = page.Row("cache");
        Assert.False(row.IsSelected);
        Assert.All(row.Steps, step => Assert.False(step.IsSelected));
    }
}
