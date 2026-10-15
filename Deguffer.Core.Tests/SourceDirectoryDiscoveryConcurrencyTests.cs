using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The source-folder rows are planned together, and each asks the one discovery they share for the
/// same roots at the same moment. The search costs a walk of the developer's whole disk on an
/// unelevated run, so a row that asks while it runs waits for it rather than starting another.
/// </summary>
public sealed class SourceDirectoryDiscoveryConcurrencyTests : IDisposable
{
    private const int Rows = 7;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task RowsAskingTogetherShareOneSearch()
    {
        // Enough indexed folders that one search takes the other rows' arrival to finish.
        var indexed = Enumerable.Range(0, 60_000)
            .Select(project => Path.Combine(_temp.Path, $"project{project}", "obj"))
            .ToList();

        var scanner = new FakeDirectoryScanner(indexed);
        var discovery = new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory());
        discovery.Include(["obj"]);

        IReadOnlyList<SourceRoot> roots = [new SourceRoot(_temp.Path)];
        using var together = new Barrier(Rows);

        var found = await Task.WhenAll(Enumerable.Range(0, Rows).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    together.SignalAndWait();
                    return discovery.FindAsync(roots);
                },
                TaskCreationOptions.LongRunning).Unwrap()))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, scanner.FindCalls);
        Assert.All(found, discovered => Assert.Same(found[0], discovered));
        Assert.Equal(indexed.Count, found[0].Candidates.Count);
    }
}
