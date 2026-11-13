using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The remove route builds its tree only inside a folder it created, removes that tree and nothing
/// beside it (§5.6), and reports each pass of the removal. The timings themselves are never asserted.
/// </summary>
public sealed class RemoveRouteTests : IDisposable
{
    private static readonly TreeShape Shape = new(Files: 10, Depth: 2, Fanout: 3);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A folder already there is not the benchmark's to delete, whatever is in it.</summary>
    [Fact]
    public void ASiteRefusesAFolderThatIsAlreadyThereAndLeavesItAsItWas()
    {
        var mine = _temp.CreateFile(10, "mine", "work.txt");

        Assert.Null(RemovalSite.Create(Path.Combine(_temp.Path, "mine"), out var error));

        Assert.StartsWith("Something already stands at that name.", error, StringComparison.Ordinal);
        Assert.True(File.Exists(mine));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(_temp.Path, "mine")));
    }

    [Fact]
    public void ASiteRefusesAFileAtTheName()
    {
        var file = _temp.CreateFile(10, "taken");

        Assert.Null(RemovalSite.Create(file, out var error));

        Assert.StartsWith("Something already stands at that name.", error, StringComparison.Ordinal);
        Assert.Equal(10, new FileInfo(file).Length);
    }

    /// <summary>The site is one new folder, never a chain of them that it would not then remove.</summary>
    [Fact]
    public void ASiteRefusesAFolderWhoseParentIsMissing()
    {
        Assert.Null(RemovalSite.Create(Path.Combine(_temp.Path, "missing", "site"), out var error));

        Assert.StartsWith("The folder's parent does not exist.", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "missing")));
    }

    [Fact]
    public void TheTreeIsBuiltToItsShape()
    {
        var root = Path.Combine(_temp.Path, "tree");

        SynthesisedTree.Build(root, Shape with { ReadOnly = true }, CancellationToken.None);

        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();

        // Three folders, each holding three: twelve below the root.
        Assert.Equal(12, folders.Count);
        Assert.Equal(12, Shape.Folders);
        Assert.Equal(3, Directory.EnumerateDirectories(root).Count());
        Assert.All(folders, folder => Assert.True(Path.GetRelativePath(root, folder).Split('\\').Length <= 2));

        Assert.Equal(10, files.Count);
        Assert.All(files, file => Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));
        Assert.All(files, file => Assert.Equal(0, new FileInfo(file).Length));
    }

    /// <summary>
    /// A file an earlier run left standing fails the build as the I/O error it is, which the run loop
    /// reports without the file's path, rather than as the parallel loop's wrapper, which it does not.
    /// </summary>
    [Fact]
    public void AFileLeftStandingFailsTheBuildAsAnInputOutputError()
    {
        var root = Path.Combine(_temp.Path, "tree");
        _temp.CreateFile(0, "tree", "d0", "f1");

        Assert.Throws<IOException>(() => SynthesisedTree.Build(root, Shape, CancellationToken.None));
    }

    /// <summary>
    /// Built and removed again on every run, and after every run the tree is gone while the site, the
    /// file kept beside the tree, and a folder outside the site are all still there (§5.6).
    /// </summary>
    [Fact]
    public async Task TheRouteRemovesWhatItBuiltAndNothingBesideIt()
    {
        var outside = _temp.CreateFile(10, "outside", "kept.txt");
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new RemoveRoute(site, Shape).Timed;

        for (var run = 0; run < 2; run++)
        {
            await route.Before!(CancellationToken.None);
            Assert.True(Directory.Exists(site.Place("tree")));

            var tally = await route.Run(CancellationToken.None);
            await route.After!();

            Assert.False(Directory.Exists(site.Place("tree")));
            Assert.True(File.Exists(Path.Combine(site.Beside, "kept")));
            Assert.True(File.Exists(outside));
            Assert.True(tally.Complete);

            // Every folder and file below the root, and the root.
            Assert.Equal(Shape.Folders + Shape.Files + 1, tally.Items);
        }

        Assert.Equal(["kept"], Directory.EnumerateFileSystemEntries(site.Beside).Select(Path.GetFileName));
    }

    /// <summary>A read-only file is one the removal has to clear before it can delete it, and it still goes.</summary>
    [Fact]
    public async Task AReadOnlyTreeIsRemovedInFull()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new RemoveRoute(site, Shape with { ReadOnly = true }).Timed;

        await route.Before!(CancellationToken.None);
        var tally = await route.Run(CancellationToken.None);

        Assert.True(tally.Complete);
        Assert.False(Directory.Exists(site.Place("tree")));
    }

    /// <summary>
    /// Each pass is the removal's own measure of itself, with what it dealt with: the walk gathers the
    /// whole tree, the file pass tries every file, and the folder pass every folder and the root.
    /// </summary>
    [Fact]
    public async Task EachPassIsReportedWithWhatItDealtWith()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new RemoveRoute(site, Shape).Timed;

        await route.Before!(CancellationToken.None);
        var passes = (await route.Run(CancellationToken.None)).Passes!;

        Assert.Equal(Shape.Files, passes.Files.Entries);
        Assert.Equal(Shape.Folders + 1, passes.Folders.Entries);
        Assert.Equal(Shape.Folders + Shape.Files + 1, passes.Walk.Entries);
        Assert.True(passes.Walk.Elapsed > TimeSpan.Zero);
        Assert.True(passes.Files.Elapsed > TimeSpan.Zero);
        Assert.True(passes.Folders.Elapsed > TimeSpan.Zero);
    }

    /// <summary>A removal that reached beside its target is a defect to stop on, never a faster run.</summary>
    [Fact]
    public async Task AKeptFileThatIsGoneAfterARunStopsTheBenchmark()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new RemoveRoute(site, Shape).Timed;

        await route.Before!(CancellationToken.None);
        await route.Run(CancellationToken.None);
        File.Delete(Path.Combine(site.Beside, "kept"));

        Assert.Contains("§5.6", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await route.After!())).Message, StringComparison.Ordinal);
    }

    /// <summary>A run cancelled part-way is not a result, so it is thrown for the run loop to report.</summary>
    [Fact]
    public async Task ACancelledRunIsThrownRatherThanReported()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new RemoveRoute(site, Shape).Timed;
        await route.Before!(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await route.Run(new CancellationToken(canceled: true)));
    }

    /// <summary>When the runs end the site goes, with whatever a run left in it, and nothing beside it does.</summary>
    [Fact]
    public async Task RemovingTheSiteTakesItAndNothingBesideIt()
    {
        var outside = _temp.CreateFile(10, "outside", "kept.txt");
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        await new RemoveRoute(site, Shape).Timed.Before!(CancellationToken.None);

        Assert.True(site.RemoveAll());

        Assert.False(Directory.Exists(site.Root));
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(_temp.Path));
    }
}
