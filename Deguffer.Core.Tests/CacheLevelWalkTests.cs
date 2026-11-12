using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the walk makes of each level's own directory before it lists anything: a link is declined
/// and never listed, an absent level is nothing, and a level Windows would not describe is named as
/// unreached. Every provider that walks a level table reaches these through its own fixture, so they
/// are held here once, against the walk itself.
/// </summary>
public sealed class CacheLevelWalkTests
{
    private const string Recognised = "Code Cache";
    private const string Stranger = "Notes";

    private static readonly IReadOnlyList<CacheLevel> Levels =
    [
        new CacheLevel("cache", new DisposableChildSet(
        [
            new ChildClassification(Recognised, SafetyTier.RegenerableCache, "Rebuilt on demand."),
        ])),
    ];

    /// <summary>
    /// The baseline the link case is measured against: the same names, in a real directory, are
    /// targeted and spared, so a link that yields neither is declined and not merely empty.
    /// </summary>
    [Fact]
    public void ADirectoryLevelIsListedAndClassified()
    {
        using var temp = new TempDirectory();
        var level = temp.CreateDirectory("root", "cache");
        temp.CreateDirectory("root", "cache", Recognised);
        temp.CreateDirectory("root", "cache", Stranger);

        var walk = CacheLevelWalk.Under(Levels, Path.Combine(temp.Path, "root"));

        Assert.Equal([Path.Combine(level, Recognised)], walk.Targets.Select(t => t.Path));
        Assert.Equal([Path.Combine(level, Stranger)], walk.Unrecognised.Select(u => u.Path));
        Assert.All(walk.Targets, t => Assert.False(t.Path.StartsWith(@"\\?\", StringComparison.Ordinal), t.Path));
        Assert.False(walk.Unreadable);
    }

    [Fact]
    public void ALevelThatIsALinkIsDeclinedAndNeverListed()
    {
        using var temp = new TempDirectory();
        var elsewhere = temp.CreateDirectory("elsewhere");
        temp.CreateDirectory("elsewhere", Recognised);
        temp.CreateDirectory("elsewhere", Stranger);
        temp.CreateDirectory("root");
        var level = Path.Combine(temp.Path, "root", "cache");
        Junction.ToDirectory(level, elsewhere);

        var walk = CacheLevelWalk.Under(Levels, Path.Combine(temp.Path, "root"));

        Assert.Empty(walk.Targets);
        Assert.Empty(walk.Unrecognised);
        Assert.Equal([(level, CacheLevelWalk.LinkReason)], walk.Declined);
        Assert.Equal([CacheLevelWalk.Note(level)], walk.Notes);
        Assert.False(walk.Unreadable);
    }

    [Fact]
    public void AnAbsentLevelIsNothingToReport()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("root");

        var walk = CacheLevelWalk.Under(Levels, Path.Combine(temp.Path, "root"));

        Assert.Empty(walk.Targets);
        Assert.Empty(walk.Declined);
        Assert.Empty(walk.Notes);
        Assert.False(walk.Unreadable);
    }

    /// <summary>
    /// Refused rather than declined as a link, which is what the fail-closed link check alone would
    /// have answered for a directory Windows would not describe.
    /// </summary>
    [Fact]
    public void ALevelWindowsWouldNotDescribeIsNamedAsUnreached()
    {
        using var temp = new TempDirectory();
        var level = temp.CreateDirectory("root", "cache");
        temp.CreateDirectory("root", "cache", Recognised);

        using var denied = DeniedDirectory.WithUnreadableAttributes(level);

        var walk = CacheLevelWalk.Under(Levels, Path.Combine(temp.Path, "root"));

        Assert.Empty(walk.Targets);
        Assert.Empty(walk.Declined);
        Assert.Equal([UnreadableRoot.UnreachedNote(level)], walk.Notes);
        Assert.True(walk.Unreadable);
    }

    /// <summary>
    /// The walk keeps what its listings returned, so a plan's record of a child it spared or a link it
    /// declined is taken from there. A level that is itself a link was never listed, so it is not
    /// among them and is asked of the disk.
    /// </summary>
    [Fact]
    public void TheWalkKeepsWhatItsListingsReturned()
    {
        using var temp = new TempDirectory();
        var level = temp.CreateDirectory("root", "cache");
        var recognised = temp.CreateDirectory("root", "cache", Recognised);
        var stranger = temp.CreateDirectory("root", "cache", Stranger);
        var link = Path.Combine(level, "link");
        Junction.ToDirectory(link, stranger);

        var walk = CacheLevelWalk.Under(Levels, Path.Combine(temp.Path, "root"));

        Assert.All([recognised, stranger, link], path => Assert.True(walk.Listed!.TryGet(path, out _), path));
        Assert.False(walk.Listed!.TryGet(level, out _));
    }
}
