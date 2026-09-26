using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The link check a path that was <em>built</em> needs. A target assembled from an application-data
/// root plus a few constants has passed through no enumeration, so a junction at any segment of it
/// puts the deletion on the far side while every §5.6 survivor named below resolves through the same
/// link and passes.
/// </summary>
public sealed class DerivedPathTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FindsALinkPartWayDownRatherThanOnlyAtTheTarget()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("elsewhere", "Cache");

        var logs = Path.Combine(root, "Logs");
        SymbolicLink.ToDirectory(logs, Path.Combine(_temp.Path, "elsewhere"));

        Assert.Equal(
            new DerivedPathObstacle(logs, IsLink: true),
            DerivedPath.FirstObstacleBetween(root, Path.Combine(logs, "Cache")));
    }

    /// <summary>
    /// A segment Windows will not describe is reported as an obstacle that is <em>not</em> a link.
    ///
    /// <para>Every caller renders a link as a fact: "it is a link to somewhere else". Windows
    /// refusing to describe a segment is not that fact, and
    /// <see cref="LongPath.IsReparsePoint"/> answers true for it because it fails closed — which is
    /// right for a predicate guarding a deletion and would be a specific claim about somebody's
    /// machine if it were rendered. The probe is asked first so that it never is, and that ordering
    /// is what this pins.</para>
    ///
    /// <para>The obstacle carries which of the two it met for the same reason. A form of this that
    /// answered only "is there a link" had to say no here, and no is what every caller reads as
    /// "carry on" — so a refused segment stopped stopping the plan.</para>
    /// </summary>
    [Fact]
    public void DoesNotCallASegmentWindowsWillNotDescribeALink()
    {
        var root = _temp.CreateDirectory("root");
        var logs = _temp.CreateDirectory("root", "Logs");
        _temp.CreateDirectory("root", "Logs", "Cache");

        using var denied = DeniedDirectory.WithUnreadableAttributes(logs);

        // The fail-closed predicate would answer "link" if it were asked without the probe ahead
        // of it, which is the whole reason the probe is ahead of it.
        Assert.True(LongPath.IsReparsePoint(logs));

        Assert.Equal(
            new DerivedPathObstacle(logs, IsLink: false),
            DerivedPath.FirstObstacleBetween(root, Path.Combine(logs, "Cache")));
    }

    /// <summary>
    /// A segment that is simply not there stops the walk and is reported as nothing. It is not a
    /// link, it hides nothing, and the caller's own probe for the target says what its absence
    /// means.
    /// </summary>
    [Fact]
    public void ReportsNothingForASegmentThatIsNotThere()
    {
        var root = _temp.CreateDirectory("root");

        Assert.Null(DerivedPath.FirstObstacleBetween(root, Path.Combine(root, "Logs", "Cache")));
    }

    [Fact]
    public void ReportsNothingWhenEverySegmentIsAnOrdinaryDirectory()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "Logs", "Cache");

        Assert.Null(DerivedPath.FirstObstacleBetween(root, Path.Combine(root, "Logs", "Cache")));
    }
}
