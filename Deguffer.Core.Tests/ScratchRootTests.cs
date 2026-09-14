using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sweep that clears scratch trees an earlier run could not remove.
///
/// <para>Its whole risk is taking something it should not, so most of these cases are negatives: the
/// root survives, a tree this run is still using survives, and a child the suite did not write
/// survives however old it is. That last one is §5.2's "recognised children only" turned on the
/// suite's own scratch, and TEMP is exactly the kind of shared place the rule exists for.</para>
/// </summary>
public sealed class ScratchRootTests : IDisposable
{
    private static readonly TimeSpan OlderThan = TimeSpan.FromHours(1);

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

    [Fact]
    public void NeverTargetsTheRootItself()
    {
        Age(Tree(), TimeSpan.FromHours(2));

        ScratchRoot.SweepStale(_temp.Path, OlderThan);

        Assert.True(Directory.Exists(_temp.Path));
    }

    /// <summary>
    /// A tree something else still holds open costs the sweep nothing: it is left where it is, the
    /// sweep does not throw, and the trees after it in the root still go.
    /// </summary>
    [Fact]
    public void LeavesAHeldTreeAndClearsTheRest()
    {
        var held = Age(Tree(), TimeSpan.FromHours(2));
        var free = Age(Tree(), TimeSpan.FromHours(2));

        using (new FileStream(
            HoldableFile(held), FileMode.Create, FileAccess.Write, FileShare.None))
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

    /// <summary>Making a scratch tree is what triggers the sweep, so a run cleans up after the last.</summary>
    [Fact]
    public void SweepsWhenAScratchTreeIsMade()
    {
        using var made = new TempDirectory();

        Assert.True(ScratchRoot.HasSwept);
    }

    /// <summary>The default root and threshold, against the real TEMP the suite actually leaks into.</summary>
    [Fact]
    public void ClearsAnAgedTreeUnderTheRealRoot()
    {
        var stale = Path.Combine(ScratchRoot.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        Age(stale, ScratchRoot.StaleAfter + TimeSpan.FromMinutes(1));

        ScratchRoot.SweepStale(ScratchRoot.Path, ScratchRoot.StaleAfter);

        Assert.False(Directory.Exists(stale));
    }

    /// <summary>A child named the way <see cref="TempDirectory"/> names one, with a file inside it.</summary>
    private string Tree()
    {
        var tree = _temp.CreateDirectory(Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(Path.Combine(tree, "content.bin"), new byte[8]);

        return tree;
    }

    private static string HoldableFile(string tree) => Path.Combine(tree, "held.bin");

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
