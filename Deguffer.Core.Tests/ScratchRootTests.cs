using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sweep that clears scratch trees an earlier run could not remove.
///
/// <para>Its whole risk is taking something it should not, so most of these cases are negatives: the
/// root survives, a tree this run is still using survives, a child the suite did not write survives
/// however old it is, and a child whose age cannot be read survives. That third one is §5.2's
/// "recognised children only" turned on the suite's own scratch, and TEMP is exactly the kind of
/// shared place the rule exists for.</para>
/// </summary>
public sealed class ScratchRootTests : IDisposable
{
    private static readonly TimeSpan OlderThan = TimeSpan.FromHours(1);

    /// <summary>The lowest and highest names the recogniser accepts, so a test can fix the sweep's order.</summary>
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000000");

    private static readonly Guid Last = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void RemovesATreeAnEarlierRunLeft()
    {
        var stale = Age(Tree(), TimeSpan.FromHours(2));

        ScratchRoot.SweepStale(_temp.Path, OlderThan);

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void LeavesATreeThisRunIsStillUsing()
    {
        var live = Tree();

        ScratchRoot.SweepStale(_temp.Path, OlderThan);

        Assert.True(Directory.Exists(live));
    }

    /// <summary>
    /// A child the suite did not write survives, whatever its age. TEMP belongs to everything on the
    /// machine, and a name this class cannot account for is not the suite's to remove.
    /// </summary>
    [Fact]
    public void LeavesAChildItDoesNotRecognise()
    {
        var theirs = Age(_temp.CreateDirectory("notes"), TimeSpan.FromDays(30));

        Assert.False(ScratchRoot.IsScratchTree("notes"));

        ScratchRoot.SweepStale(_temp.Path, OlderThan);

        Assert.True(Directory.Exists(theirs));
    }

    /// <summary>
    /// The root goes nowhere, and the age filter is not what saves it. The root swept here carries a
    /// name the recogniser accepts and a creation time past the cutoff, so it meets both tests a
    /// child has to meet, and the only thing between it and the delete is that a root is never a
    /// candidate.
    ///
    /// <para>It is a level below this test's own scratch tree for that reason: back-dating a direct
    /// child of the real root would offer it to a sweep running in a concurrent test process.</para>
    /// </summary>
    [Fact]
    public void NeverTargetsTheRootItself()
    {
        var root = Age(Tree(), TimeSpan.FromHours(2));
        var child = Age(Tree(root), TimeSpan.FromHours(2));

        Assert.True(ScratchRoot.IsScratchTree(Path.GetFileName(root)));

        ScratchRoot.SweepStale(root, OlderThan);

        // The child as well as the root: without it a sweep that did nothing at all would pass.
        Assert.False(Directory.Exists(child));
        Assert.True(Directory.Exists(root));
    }

    /// <summary>
    /// A tree something else holds open does not stop the sweep: it stays, and the trees listed
    /// after it still go.
    ///
    /// <para>The names are chosen rather than random, because the sweep works in the order the root
    /// lists its children and a random pair would put the held one second about half the time. The
    /// test asserts that order before it relies on it. It asserts only that the held tree survives,
    /// not that it is untouched: a recursive delete removes what it can before the refusal, so a
    /// refused tree may come back part empty, and the sweep promises nothing more than that a
    /// refusal costs it nothing.</para>
    /// </summary>
    [Fact]
    public void LeavesAHeldTreeAndClearsTheRest()
    {
        var held = Age(Tree(First), TimeSpan.FromHours(2));
        var free = Age(Tree(Last), TimeSpan.FromHours(2));

        Assert.Equal(held, Directory.EnumerateDirectories(_temp.Path).First());

        using (new FileStream(Path.Combine(held, "held.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ScratchRoot.SweepStale(_temp.Path, OlderThan);
        }

        Assert.True(Directory.Exists(held));
        Assert.False(Directory.Exists(free));
    }

    /// <summary>
    /// A machine whose first run has not made the root yet still sweeps. Not throwing is the whole
    /// assertion: the sweep runs before the first tree is created, so the root may not be there.
    /// </summary>
    [Fact]
    public void DoesNothingWhenTheRootWasNeverMade()
    {
        ScratchRoot.SweepStale(Path.Combine(_temp.Path, "absent"), OlderThan);
    }

    /// <summary>
    /// A root the account may not list costs the run nothing either.
    ///
    /// <para>Not throwing is again the whole assertion, and it carries further than it looks:
    /// <see cref="ScratchRoot.SweepOnce"/> runs the sweep inside a <see cref="Lazy{T}"/>, which
    /// keeps a faulting factory's exception for the life of the process. One throw here would be
    /// re-thrown by every <see cref="TempDirectory"/> the run went on to make.</para>
    /// </summary>
    [Fact]
    public void DoesNotThrowWhenItCannotListTheRoot()
    {
        var root = _temp.CreateDirectory("unreadable");
        using var denied = new DeniedDirectory(root);

        ScratchRoot.SweepStale(root, OlderThan);
    }

    /// <summary>
    /// Windows dates an entry that is not there as 1601, which is older than any cutoff, so a sweep
    /// that compared it straight would read "there is nothing here to date" as "certainly stale".
    /// The entry going between the listing and the age read is the ordinary way to reach it.
    /// </summary>
    [Fact]
    public void WillNotCallAChildStaleWhenThereIsNoAgeToRead()
    {
        var vanished = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));

        Assert.Equal(DateTime.FromFileTimeUtc(0), Directory.GetCreationTimeUtc(vanished));
        Assert.False(ScratchRoot.IsStale(vanished, DateTime.UtcNow - OlderThan));
    }

    /// <summary>
    /// The other half of the same rule, and the one that is not a value: a child whose attributes
    /// the account may not read throws rather than answering 1601.
    ///
    /// <para>Asked of <see cref="ScratchRoot.IsStale"/> directly rather than through the sweep,
    /// because making a child's attributes unreadable takes a rule on the directory above it too,
    /// and that one stops the sweep listing the root at all. There is no fixture that produces the
    /// refused child without also producing the unreadable root.</para>
    /// </summary>
    [Fact]
    public void WillNotCallAChildStaleWhenTheAgeReadIsRefused()
    {
        var child = Age(Tree(), TimeSpan.FromHours(2));
        using var denied = DeniedDirectory.WithUnreadableAttributes(child);

        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetCreationTimeUtc(child));
        Assert.False(ScratchRoot.IsStale(child, DateTime.UtcNow - OlderThan));
    }

    /// <summary>Sweeping is what a scratch tree's own constructor sets off, so a run clears the last one's leavings.</summary>
    /// <remarks>
    /// The trigger is <see cref="_temp"/>, built before this method by xUnit. There is nothing to
    /// arrange: take the call out of <see cref="TempDirectory"/>'s constructor and nothing in the
    /// process sets this at all.
    /// </remarks>
    [Fact]
    public void SweepsWhenAScratchTreeIsMade() => Assert.True(ScratchRoot.HasSwept);

    /// <summary>
    /// The root the sweep is pointed at is the root scratch trees are actually made under, asked of
    /// the two sides rather than of a literal, so they cannot drift apart.
    ///
    /// <para>Nothing here back-dates a direct child of the real root, and nothing sweeps it. Doing
    /// either would put a tree belonging to a test process running beside this one in reach of a
    /// sweep, and the age margin is the only thing keeping it out.</para>
    /// </summary>
    [Fact]
    public void MakesEveryScratchTreeUnderTheRoot()
    {
        using var made = new TempDirectory();

        Assert.Equal(ScratchRoot.Path, Path.GetDirectoryName(made.Path));
    }

    /// <summary>
    /// The recogniser accepts what <see cref="TempDirectory"/> writes, asked of the writer itself
    /// rather than of a literal, so the two cannot drift apart.
    /// </summary>
    [Fact]
    public void RecognisesWhatAScratchTreeIsActuallyNamed()
    {
        using var made = new TempDirectory();

        Assert.True(ScratchRoot.IsScratchTree(Path.GetFileName(made.Path)));
    }

    /// <summary>
    /// Names the recogniser has to refuse. The first two are what a shape test alone would let
    /// through: <see cref="Guid.TryParseExact(string, string, out Guid)"/> trims its input and
    /// accepts either case, and <see cref="TempDirectory"/> writes neither.
    /// </summary>
    [Theory]
    [InlineData(" 0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("notes")]
    [InlineData("")]
    public void RefusesANameNoScratchTreeWouldCarry(string name) =>
        Assert.False(ScratchRoot.IsScratchTree(name));

    /// <summary>A child of <paramref name="root"/>, named the way <see cref="TempDirectory"/> names a tree.</summary>
    private static string Tree(string root, Guid? name = null)
    {
        var tree = Directory.CreateDirectory(
            Path.Combine(root, (name ?? Guid.NewGuid()).ToString("N"))).FullName;

        File.WriteAllBytes(Path.Combine(tree, "content.bin"), new byte[8]);

        return tree;
    }

    /// <summary>A child of this test's own scratch tree, on the same terms.</summary>
    private string Tree(Guid? name = null) => Tree(_temp.Path, name);

    /// <summary>
    /// Push <paramref name="directory"/>'s creation time back, which is the only time the sweep
    /// reads. Its last-write time is deliberately left alone: a tree whose contents were written
    /// minutes ago is still an earlier run's leaving, and the sweep has to say so.
    /// </summary>
    private static string Age(string directory, TimeSpan by)
    {
        Directory.SetCreationTimeUtc(LongPath.Extended(directory), DateTime.UtcNow - by);

        return directory;
    }
}
