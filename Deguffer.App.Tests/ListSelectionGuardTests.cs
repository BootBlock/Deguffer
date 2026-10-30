using Deguffer.App.Shell;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the list guard Explore and Memory share holds <see cref="ListGestures"/> together: what counts
/// as the page writing, when a write leaves the list settled, and when a restore runs. The rule itself
/// is tested in Core.
/// </summary>
public sealed class ListSelectionGuardTests
{
    private readonly Queue<Action> _later = new();

    private bool _rewriting;

    [Fact]
    public void ATouchedListAdmitsItsReport()
    {
        var guard = Guard();

        guard.Keyed();

        Assert.True(guard.Admits(Unexpected));
        Assert.Empty(_later);
    }

    [Fact]
    public void AReportInsideThePagesOwnWriteIsRefusedAndNotRestored()
    {
        var guard = Guard();
        guard.Keyed();

        guard.Write(() => Assert.False(guard.Admits(Unexpected)));

        Assert.Empty(_later);
    }

    /// <summary>A write inside a write, as Explore swaps the rows and then writes the highlight, is still the page's.</summary>
    [Fact]
    public void AWriteInsideAWriteKeepsThePageWritingUntilTheOuterEnds()
    {
        var guard = Guard();

        guard.Write(() =>
        {
            guard.Write(() => { });
            guard.Keyed();

            Assert.False(guard.Admits(Unexpected));
        });

        Assert.Empty(_later);
    }

    [Fact]
    public void AReportWhileTheViewModelRewritesTheRowsIsRefusedAndNotRestored()
    {
        var guard = Guard();
        guard.Keyed();
        _rewriting = true;

        Assert.False(guard.Admits(Unexpected));
        Assert.Empty(_later);
    }

    [Fact]
    public void AWriteLeavesTheListSettled()
    {
        var guard = Guard();
        guard.Keyed();

        guard.Write(() => { });

        Assert.Equal(ListGesture.Settled, guard.Gesture);
    }

    /// <summary>The page's write is over even where it threw, and the list must not stay refused for good.</summary>
    [Fact]
    public void AWriteThatThrowsStillEnds()
    {
        var guard = Guard();

        Assert.Throws<InvalidOperationException>(() => guard.Write(() => throw new InvalidOperationException()));

        guard.Keyed();
        Assert.True(guard.Admits(Unexpected));
    }

    [Fact]
    public void TheControlsOwnHighlightIsPutBackLaterNotInsideItsReport()
    {
        var guard = Guard();
        var restored = 0;

        Assert.False(guard.Admits(() => restored++));
        Assert.Equal(0, restored);

        _later.Dequeue()();

        Assert.Equal(1, restored);
    }

    /// <summary>A press landing between the control's report and the restore is the reader taking the list back.</summary>
    [Fact]
    public void ARestoreIsDroppedWhereTheReaderTouchedTheListFirst()
    {
        var guard = Guard();
        var restored = 0;

        guard.Admits(() => restored++);
        guard.Pressed(onRow: true);

        _later.Dequeue()();

        Assert.Equal(0, restored);
    }

    [Fact]
    public void ARewriteDuringAPressOnARowKeepsTheClick()
    {
        var guard = Guard();

        guard.Pressed(onRow: true);
        guard.Write(() => { });
        guard.Released();

        Assert.True(guard.Admits(Unexpected));
    }

    private ListSelectionGuard Guard() => new(() => _rewriting, _later.Enqueue);

    private static void Unexpected() => Assert.Fail("Nothing was to be restored.");
}
