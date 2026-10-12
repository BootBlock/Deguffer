using System.Collections.Frozen;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk an approved source root takes when the volume index cannot answer: what it offers, what
/// it never enters, and what it says it could not search, the same at one thread as at many.
///
/// <para>No test here claims §6.3. The walk hands back display paths only, and lists every folder
/// through <c>BoundedFileWalk</c>, whose own tests assert the form of the paths it visits.</para>
/// </summary>
public sealed class SourceTreeWalkTests : IDisposable
{
    private static readonly DirectorySearch Sought =
        SourceTreeBoundary.Searching(FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "obj", "target"));

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The workers find candidates in whatever order they run, so the result is sorted. One worker
    /// takes the tree a level at a time, which finds <c>deep\a\b\c\target</c> last and
    /// <c>nested\obj</c> first, so the order asserted here is one no unsorted walk produces.
    ///
    /// <para>The §5.6 negative is the rest of the list: nothing inside <c>.git</c> or
    /// <c>node_modules</c>, nothing through a junction, nothing below a candidate, and nothing inside
    /// a folder the walk was refused, which is named instead.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(WalkTuning.MaximumThreads)]
    public void FindsTheSameCandidatesInTheSameOrderAtAnyThreadCount(int threads)
    {
        var root = _temp.CreateDirectory("root");
        var expected = new List<string>();

        for (var i = 0; i < 30; i++)
        {
            _temp.CreateFile(10, "root", $"p{i:D2}", "obj", "a.dll");
            _temp.CreateDirectory("root", $"p{i:D2}", "src");
            expected.Add(Path.Combine(root, $"p{i:D2}", "obj"));
        }

        expected.Add(_temp.CreateDirectory("root", "deep", "a", "b", "c", "target"));
        expected.Add(Path.GetDirectoryName(_temp.CreateDirectory("root", "nested", "obj", "obj"))!);

        _temp.CreateDirectory("root", ".git", "objects", "obj");
        _temp.CreateDirectory("root", "web", "node_modules", "left-pad", "obj");
        _temp.CreateDirectory("outside", "obj");
        Junction.ToDirectory(Path.Combine(root, "linked"), Path.Combine(_temp.Path, "outside"));
        var refused = _temp.CreateDirectory("root", "refused");
        _temp.CreateDirectory("root", "refused", "obj");

        using var denied = new DeniedDirectory(refused);

        var walked = SourceTreeWalk.Under(root, Sought, new WalkTuning(threads, WalkTuning.Default.ListingBufferBytes), CancellationToken.None);

        Assert.Equal(expected.Order(StringComparer.Ordinal), walked.Candidates);
        Assert.Equal([refused], walked.Unreadable);
        Assert.True(walked.RootListed);
    }

    /// <summary>
    /// A folder that is not there holds nothing, and that is a complete answer. Reporting it as
    /// unreadable would put a sentence about permissions against a build output removed while the walk
    /// ran, and a root reported unlisted would make a race wait for the index over nothing.
    /// </summary>
    [Fact]
    public void AFolderThatIsGoneIsEmptyRatherThanUnreadable()
    {
        var walked = SourceTreeWalk.Under(
            Path.Combine(_temp.Path, "never-created"), Sought, WalkTuning.Default, CancellationToken.None);

        Assert.Empty(walked.Candidates);
        Assert.Empty(walked.Unreadable);
        Assert.True(walked.RootListed);
    }

    /// <summary>A root that will not be listed is named, and is said not to have been listed.</summary>
    [Fact]
    public void ARootThatWillNotBeListedIsNamedAndNotListed()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "obj");

        using var denied = new DeniedDirectory(root);

        var walked = SourceTreeWalk.Under(root, Sought, WalkTuning.Default, CancellationToken.None);

        Assert.Empty(walked.Candidates);
        Assert.Equal([root], walked.Unreadable);
        Assert.False(walked.RootListed);
    }
}
