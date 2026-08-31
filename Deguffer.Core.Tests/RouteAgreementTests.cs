using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's two routes, asked the same question about the same tree.
///
/// Everywhere else in the suite the fast path is tested against a synthesised table and the walk
/// against a real directory, so the two are never compared — and a disagreement between them is
/// invisible from either side. It is also the disagreement that matters most: which route runs
/// depends on the process token, so a divergence here means the same machine is described two
/// different ways depending on how the user happened to start the app.
/// </summary>
public class RouteAgreementTests
{
    private static DirectoryScanner Walking() =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

    private static DirectoryScanner Indexing(string path, MftFixture fixture) =>
        new(FakeMftSourceFactory.Serving(Path.GetFullPath(path)[0], fixture));

    [Fact]
    public async Task TheTwoRoutesTotalOneTreeToTheSameNumber()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "cache",
            new TreeFile("a.tgz", 4096),
            new TreeFile("empty.tmp", 0),
            new TreeDirectory(
                "content-v2",
                new TreeFile("b.tgz", 8000),
                new TreeDirectory("no-entries")),
            new TreeDirectory("sha512", new TreeFile("c.tgz", 1234))));

        var walked = await Walking().MeasureAsync(path);
        var indexed = await Indexing(path, fixture).MeasureAsync(path);

        // The routes have to have been different ones, or this compares a number with itself.
        Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
        Assert.Equal(ScanStrategy.MasterFileTable, indexed.Strategy);

        Assert.Equal(4096 + 8000 + 1234, walked.Size.Logical);
        Assert.Equal(walked.Size.Logical, indexed.Size.Logical);
        Assert.Equal(walked.Size.Reclaimable, indexed.Size.Reclaimable);
    }

    /// <summary>
    /// The one disagreement that is meant to be there. A file small enough to live inside its own
    /// MFT record occupies no clusters, so deleting it frees none — the walk cannot see that and
    /// reports its length. Pinned rather than left to be discovered: it is the reason an elevated
    /// scan can offer less than an unelevated one, and a future change that hides it would be
    /// hiding an honest number behind a flattering one.
    /// </summary>
    [Fact]
    public async Task TheRoutesDisagreeOnResidentFilesAndTheIndexIsTheHonestOne()
    {
        using var temp = new TempDirectory();

        var (path, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "cache",
            new TreeFile("index.json", 300, Resident: true)));

        var walked = await Walking().MeasureAsync(path);
        var indexed = await Indexing(path, fixture).MeasureAsync(path);

        Assert.Equal(300, walked.Size.Reclaimable);
        Assert.Equal(0, indexed.Size.Reclaimable);

        // Both agree on what re-downloading would cost; they differ only on what the volume gives
        // back, which is the number the user is shown.
        Assert.Equal(300, walked.Size.Logical);
        Assert.Equal(300, indexed.Size.Logical);
    }

    /// <summary>
    /// Discovery has two routes for the same reason measurement does, and the same obligation. The
    /// index knows every directory on the volume, so the limits the walk gets from not descending
    /// have to be applied to its answers deliberately — otherwise an elevated run offers to delete
    /// directories an unelevated one would never have found.
    /// </summary>
    [Fact]
    public async Task TheTwoRoutesFindTheSameDirectoriesInASourceRoot()
    {
        using var temp = new TempDirectory();

        var (root, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "src",
            new TreeDirectory(
                "Example",
                new TreeDirectory("obj", new TreeDirectory("Debug", new TreeDirectory("obj"))),
                new TreeDirectory("bin")),
            new TreeDirectory(".git", new TreeDirectory("obj")),
            new TreeDirectory("node_modules", new TreeDirectory("left-pad", new TreeDirectory("obj")))));

        var walked = await new ObjDirectoryDiscovery(Walking()).FindAsync("obj", [root]);
        var indexed = await new ObjDirectoryDiscovery(Indexing(root, fixture)).FindAsync("obj", [root]);

        Assert.False(walked.UsedIndex);
        Assert.True(indexed.UsedIndex);

        Assert.Equal([Path.Combine(root, "Example", "obj")], walked.Candidates);
        Assert.Equal(walked.Candidates, indexed.Candidates);
    }

    /// <summary>
    /// §6.3: the same agreement past MAX_PATH, because the index route decides what to keep by
    /// taking a path apart. A truncation there is not a crash — it is a candidate list quietly
    /// disagreeing with the walk's, which is the one thing these tests exist to rule out.
    /// </summary>
    [Fact]
    public async Task TheTwoRoutesAgreeOnDirectoriesPastMaxPath()
    {
        const string Deep = "a-source-directory-with-a-name-long-enough-to-push-past-max-path";

        using var temp = new TempDirectory();

        var (root, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "src",
            new TreeDirectory(
                Deep,
                new TreeDirectory(
                    Deep,
                    new TreeDirectory(Deep, new TreeDirectory("obj", new TreeFile("Example.dll", 64))),
                    new TreeDirectory("node_modules", new TreeDirectory("obj"))))));

        Assert.True(Path.Combine(root, Deep, Deep, Deep, "obj").Length > 260, "the tree is not long enough to test anything");

        var walked = await new ObjDirectoryDiscovery(Walking()).FindAsync("obj", [root]);
        var indexed = await new ObjDirectoryDiscovery(Indexing(root, fixture)).FindAsync("obj", [root]);

        Assert.Equal([Path.Combine(root, Deep, Deep, Deep, "obj")], walked.Candidates);
        Assert.Equal(walked.Candidates, indexed.Candidates);
    }

    /// <summary>
    /// An approved root that is itself called <c>obj</c> is not a candidate. The walk cannot return
    /// it — it starts inside it — and the index must not either: the user approved that folder as a
    /// place to search, which is not the same as offering it up for deletion.
    /// </summary>
    [Fact]
    public async Task NeitherRouteOffersTheApprovedRootItself()
    {
        using var temp = new TempDirectory();

        var (root, fixture) = MirroredTree.Realise(temp, new TreeDirectory("obj", new TreeFile("Example.dll", 512)));

        var walked = await new ObjDirectoryDiscovery(Walking()).FindAsync("obj", [root]);
        var indexed = await new ObjDirectoryDiscovery(Indexing(root, fixture)).FindAsync("obj", [root]);

        Assert.Empty(walked.Candidates);
        Assert.Empty(indexed.Candidates);
    }
}
