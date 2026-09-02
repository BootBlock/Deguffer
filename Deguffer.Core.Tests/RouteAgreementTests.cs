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
    /// A single file, which is what <c>C:\Windows\MEMORY.DMP</c> is, and where the two routes turn
    /// out to be one.
    ///
    /// The index cannot resolve a file path — only directories carry names in it, deliberately —
    /// so an elevated run reaches the same code an unelevated one does. What made that worth pinning
    /// is that the code answered <em>zero</em> for anything that was not a directory, on both routes,
    /// which for the largest reclaim Deguffer knows about produces a step nobody can select.
    ///
    /// It is also why a file is reported as <see cref="ScanStrategy.DirectRead"/> rather than as the
    /// walk. A fallback reason explains a slow scan, one <c>stat</c> is not slow, and the elevated
    /// run would otherwise carry a sentence apologising for a walk that never happened.
    /// </summary>
    [Fact]
    public async Task TheTwoRoutesAgreeOnASingleFileAndNeitherApologisesForIt()
    {
        using var temp = new TempDirectory();

        var (directory, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "dumps", new TreeFile("MEMORY.DMP", 65536)));

        var file = Path.Combine(directory, "MEMORY.DMP");

        var walked = await Walking().MeasureAsync(file);
        var indexed = await Indexing(file, fixture).MeasureAsync(file);

        Assert.Equal(65536, walked.Size.Reclaimable);
        Assert.Equal(walked.Size.Reclaimable, indexed.Size.Reclaimable);
        Assert.Equal(walked.Size.Logical, indexed.Size.Logical);

        foreach (var result in new[] { walked, indexed })
        {
            Assert.Equal(ScanStrategy.DirectRead, result.Strategy);
            Assert.Equal(FallbackReason.None, result.Fallback);
            Assert.Null(result.FallbackNote);
        }

        // The containing directory still goes the two separate ways, so the fixture really does
        // serve the index and the comparison above is not two identical code paths by accident.
        Assert.Equal(ScanStrategy.ParallelEnumeration, (await Walking().MeasureAsync(directory)).Strategy);
        Assert.Equal(ScanStrategy.MasterFileTable, (await Indexing(directory, fixture).MeasureAsync(directory)).Strategy);
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

        var walked = await Discovery(Walking()).FindAsync([root]);
        var indexed = await Discovery(Indexing(root, fixture)).FindAsync([root]);

        Assert.False(walked.UsedIndex);
        Assert.True(indexed.UsedIndex);

        Assert.Equal([Path.Combine(root, "Example", "obj")], walked.Candidates);
        Assert.Equal(walked.Candidates, indexed.Candidates);
    }

    /// <summary>
    /// The agreement has to survive long names as well, because the index route decides what to keep
    /// by taking a path apart rather than by walking it. A disagreement there is not a crash: it is a
    /// candidate list quietly differing from the walk's, which is the one thing these tests exist to
    /// rule out.
    ///
    /// <para>The depth is not itself a §6.3 assertion. Both routes reach the deep <c>obj</c> whether
    /// or not Core prefixes anything, because .NET prefixes at 260 characters on its own — see
    /// <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>. What is
    /// established here is the agreement, not the prefix.</para>
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

        var walked = await Discovery(Walking()).FindAsync([root]);
        var indexed = await Discovery(Indexing(root, fixture)).FindAsync([root]);

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

        var walked = await Discovery(Walking()).FindAsync([root]);
        var indexed = await Discovery(Indexing(root, fixture)).FindAsync([root]);

        Assert.Empty(walked.Candidates);
        Assert.Empty(indexed.Candidates);
    }

    /// <summary>
    /// The disagreement discovery is <em>meant</em> to have, and the one this class exists to keep
    /// visible: below a directory the account may not list, the walk finds nothing and the index
    /// finds everything.
    ///
    /// <para>The walk has to enumerate, and an enumeration that is refused yields nothing rather
    /// than a partial view, so the whole subtree is out of its reach. The MFT reads file records and
    /// no ACL guards those. Reach is therefore a property of the token, not of the rules, and
    /// <see cref="SourceTreeBoundary"/> cannot equalise it — it judges a candidate by name, and a
    /// name says nothing about who may read it.</para>
    ///
    /// <para>Three things are asserted in the one denied tree, because the argument for accepting
    /// the disagreement needs all three and each is worthless alone. The walk finds nothing. The
    /// index still refuses everything the boundary refuses — a nested candidate, a directory under
    /// <c>.git</c>, one under <c>node_modules</c> — so the extra reach carries no extra licence. And
    /// what the index does offer is a whole candidate rather than a broken one: denying the right to
    /// <em>list</em> a directory leaves the right to traverse it, so the project around the
    /// candidate is readable and the candidate itself measures. That last one is the claim the whole
    /// argument rests on, and it is the one that would be invisible if it stopped being true.</para>
    /// </summary>
    [Fact]
    public async Task OnlyTheIndexReachesBelowADirectoryTheAccountMayNotList()
    {
        using var temp = new TempDirectory();

        var (root, fixture) = MirroredTree.Realise(temp, new TreeDirectory(
            "src",
            new TreeDirectory(
                "restricted",
                new TreeDirectory(
                    "Example",
                    new TreeFile("Example.csproj", 64),
                    new TreeDirectory("obj", new TreeFile("Example.dll", 128), new TreeDirectory("obj"))),
                new TreeDirectory(".git", new TreeDirectory("obj")),
                new TreeDirectory("node_modules", new TreeDirectory("left-pad", new TreeDirectory("obj"))))));

        var candidate = Path.Combine(root, "restricted", "Example", "obj");

        using var denied = new DeniedDirectory(Path.Combine(root, "restricted"));

        var walked = await Discovery(Walking()).FindAsync([root]);
        var indexed = await Discovery(Indexing(root, fixture)).FindAsync([root]);

        Assert.Empty(walked.Candidates);
        Assert.Equal([candidate], indexed.Candidates);

        // The candidate is a real one, which is what makes this reach rather than licence. A path
        // through the denied directory still resolves, so everything §5.2 recognises a build
        // directory by is readable, and the size the user is shown is measured rather than guessed.
        Assert.True(File.Exists(Path.Combine(root, "restricted", "Example", "Example.csproj")));
        Assert.Equal(128, (await Walking().MeasureAsync(candidate)).Size.Logical);
    }

    /// <summary>
    /// A discovery looking for <c>obj</c> alone, which is what these tests compare the two routes
    /// over. The names a discovery is given are part of what defines its answer, so both routes get
    /// the same ones.
    /// </summary>
    private static SourceDirectoryDiscovery Discovery(IDirectoryScanner scanner)
    {
        var discovery = new SourceDirectoryDiscovery(scanner);
        discovery.Include(["obj"]);
        return discovery;
    }
}
