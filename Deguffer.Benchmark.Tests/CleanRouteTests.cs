using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The clean route removes every build folder it built as one plan, keeps every project beside them
/// (§5.6), and reports what its steps spent their time on. Asked of an invented machine throughout.
/// </summary>
public sealed class CleanRouteTests : IDisposable
{
    private const int Trees = 3;

    private static readonly TreeShape EachTree = CleanRoute.Shape(5);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryBuildFolderGoesAndEveryProjectStays()
    {
        var outside = _temp.CreateFile(10, "outside", "kept.txt");
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = Timed(site);

        for (var run = 0; run < 2; run++)
        {
            route.Before!(CancellationToken.None);
            var tally = route.Run(CancellationToken.None);
            route.After!();

            for (var i = 0; i < Trees; i++)
            {
                var project = site.Place($"p{i}");

                Assert.False(Directory.Exists(Path.Combine(project, "obj")));
                Assert.Equal([$"p{i}.csproj"], Directory.EnumerateFileSystemEntries(project).Select(Path.GetFileName));
            }

            Assert.True(File.Exists(Path.Combine(site.Beside, "kept")));
            Assert.True(File.Exists(outside));
            Assert.True(tally.Complete);
            Assert.Equal(Trees, tally.Items);
        }
    }

    /// <summary>
    /// The steps' own times and the removals' passes, summed over the plan: what a step costs around
    /// its removal is the difference.
    /// </summary>
    [Fact]
    public void EachStepsTimeAndEachRemovalsPassesAreReported()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = Timed(site);

        route.Before!(CancellationToken.None);
        var tally = route.Run(CancellationToken.None);

        var steps = tally.Steps!;
        Assert.Equal(Trees, steps.Count);
        Assert.True(steps.Elapsed > TimeSpan.Zero);
        Assert.Equal(steps.Elapsed, steps.UseCheck + steps.Look + steps.Acting);

        Assert.Equal(Trees * EachTree.Files, tally.Passes!.Files.Entries);
        Assert.Equal(Trees * (EachTree.Folders + 1), tally.Passes.Folders.Entries);
        Assert.True(tally.Passes.Walk.Elapsed + tally.Passes.Files.Elapsed + tally.Passes.Folders.Elapsed <= steps.Acting);
    }

    /// <summary>A project file gone after a run is a defect to stop on, even where the clean's own verification passed.</summary>
    [Fact]
    public void AProjectFileThatIsGoneAfterARunStopsTheBenchmark()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = Timed(site);

        route.Before!(CancellationToken.None);
        route.Run(CancellationToken.None);
        File.Delete(Path.Combine(site.Place("p1"), "p1.csproj"));

        Assert.Contains("§5.6", Assert.Throws<InvalidOperationException>(route.After!).Message, StringComparison.Ordinal);
    }

    private RouteRun Timed(RemovalSite site) => new CleanRoute(
        site,
        Trees,
        EachTree,
        new FakeUserEnvironment(_temp.CreateDirectory("machine")),
        new FakeSystemDirectories(_temp.CreateDirectory("machine")),
        new FakeVolumeInventory()).Timed;
}
