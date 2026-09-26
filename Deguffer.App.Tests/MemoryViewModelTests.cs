using System.Collections.Specialized;
using System.ComponentModel;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Memory page holds its readings together (§7.2): what a reading rewrites, what it keeps,
/// what it says when the readings stop, and which of those changes the list is told about. Where the
/// view stands after a reading and what a pick is are Core's, and proven there.
/// </summary>
public sealed class MemoryViewModelTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly MemoryNodeKey Alpha = new(MemoryPart.Process, 100, 10);
    private static readonly MemoryNodeKey Beta = new(MemoryPart.Process, 200, 20);
    private static readonly MemoryNodeKey Applications = MemoryNodeKey.Of(MemoryPart.Applications);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Alpha, with beta running under it, so alpha holds its own share and beta.</summary>
    private static MemorySnapshot First() => new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 300, created: 10)
        .Process(200, 100, "beta.exe", 100, created: 20)
        .Build();

    /// <summary>The same two, with a larger program listed first so that every number moves.</summary>
    private static MemorySnapshot Renumbered() => new MemorySnapshotBuilder()
        .Process(50, 1, "gamma.exe", 9_000, created: 5)
        .Process(100, 1, "alpha.exe", 320, created: 10)
        .Process(200, 100, "beta.exe", 90, created: 20)
        .Build();

    private MemoryViewModel Page(IMemorySource source)
    {
        var machine = new MemoryCloseMachine();
        var actions = new MemoryActions(
            machine.Policy(), machine.Closer(), () => new ScriptedMemoryPrompt(answer: false));

        return new MemoryViewModel(
            new MemoryFeed(source, _clock),
            new MemorySelection(actions, new CrashLog(new FakeUserEnvironment(_temp.Path))));
    }

    /// <summary>A page whose readings are these, in order, with the last repeating.</summary>
    private MemoryViewModel Page(params MemorySnapshot[] readings) => Page(new QueuedMemorySource(readings));

    /// <summary>Completes when the page has next shown a reading or a move.</summary>
    private static Task ShownAsync(MemoryViewModel page)
    {
        var shown = new TaskCompletionSource();

        void OnChanged(object? sender, EventArgs e)
        {
            page.ViewChanged -= OnChanged;
            shown.SetResult();
        }

        page.ViewChanged += OnChanged;

        return shown.Task.WaitAsync(Patience);
    }

    /// <summary>Let the feed take its next reading, and wait for the page to show it.</summary>
    private async Task NextReadingAsync(MemoryViewModel page)
    {
        var shown = ShownAsync(page);

        await _clock.WhenWaitingAsync(Patience);
        _clock.Advance(MemoryFeed.Cadence);

        await shown;
    }

    private static List<NotifyCollectionChangedAction> Watch(INotifyCollectionChanged list)
    {
        var told = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, changed) => told.Add(changed.Action);

        return told;
    }

    [Fact]
    public void TheFirstReadingIsShownOnceWithItsHeadlineItsPartsAndATrailOfOne() => UiThread.Run(async () =>
    {
        var page = Page(First());
        var shows = 0;
        page.ViewChanged += (_, _) => shows++;
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        Assert.Equal(1, shows);
        Assert.NotNull(page.Tree);
        Assert.Equal(page.Tree.RootNode, page.CurrentNode);
        Assert.Equal(MemoryHeadline.Commit(page.Tree.Snapshot.System), page.Commit);
        Assert.Equal(page.Tree.ChildrenOf(page.Tree.RootNode).Length, page.Rows.Count);
        Assert.Single(page.Trail);
        Assert.False(page.CanAscend);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>
    /// A reading is the same thing measured again, so the reader stays where they were and the list is
    /// brought up to date in place: a reset throws away the scroll position twice a second.
    /// </summary>
    [Fact]
    public void AReadingKeepsTheReaderWhereTheyWereAndRewritesTheListInPlace() => UiThread.Run(async () =>
    {
        var page = Page(First(), Renumbered());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        // Alpha, whose number the next reading moves, so standing on it by number would not carry.
        var standing = page.Tree!.Find(Alpha)!.Value;
        page.Descend(standing);
        var betaRow = page.Rows.Single(row => row.Key == Beta);
        var told = Watch(page.Rows);

        await NextReadingAsync(page);

        Assert.NotEqual(standing, page.Tree.Find(Alpha));
        Assert.Equal(page.Tree.Find(Alpha), page.CurrentNode);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, told);
        Assert.Same(betaRow, page.Rows.Single(row => row.Key == Beta));
        Assert.Equal(page.Tree.Find(Beta), betaRow.Node);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>
    /// Another node's rows have nothing to do with these, so the list starts again, and a program
    /// picked in one part is not picked in the next.
    /// </summary>
    [Fact]
    public void MovingElsewhereStartsTheListAgainAndDropsThePick() => UiThread.Run(async () =>
    {
        var page = Page(First());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        page.Selection.Select(page.Tree!.Find(MemoryNodeKey.Of(MemoryPart.Windows)));
        var told = Watch(page.Rows);

        page.Descend(page.Tree.Find(Applications)!.Value);

        Assert.Contains(NotifyCollectionChangedAction.Reset, told);
        Assert.False(page.Selection.HasSelection);
        Assert.True(page.CanAscend);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>A reading carries the pick onto the program's new number rather than onto whatever took its old one.</summary>
    [Fact]
    public void AReadingCarriesThePickToWhereTheProgramNowIs() => UiThread.Run(async () =>
    {
        var page = Page(First(), Renumbered());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        var before = page.Tree!.Find(Alpha);
        page.Selection.Select(before);

        await NextReadingAsync(page);

        Assert.NotEqual(before, page.Tree.Find(Alpha));
        Assert.Equal(page.Tree.Find(Alpha), page.Selection.Node);

        await leave.CancelAsync();
        await watching;
    });

    [Fact]
    public void OpeningWhatHoldsNothingOrWhatIsNotThereMovesNothing() => UiThread.Run(async () =>
    {
        var page = Page(First());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        var shows = 0;
        page.ViewChanged += (_, _) => shows++;
        var beta = page.Tree!.Find(Beta)!.Value;

        page.Descend(beta);
        page.Descend(-1);
        page.Descend(page.Tree.NodeCount);

        Assert.Equal(0, shows);
        Assert.Equal(page.Tree.RootNode, page.CurrentNode);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>
    /// A control drops an item from its own selection when the list under it moves, and reports that
    /// as a selection change. The page tells that apart from a gesture only by this flag, so every
    /// change a reading makes to the list happens under it.
    /// </summary>
    [Fact]
    public void EveryChangeAReadingMakesToTheListIsMadeWhileTheFlagIsUp() => UiThread.Run(async () =>
    {
        var page = Page(First(), Renumbered());
        using var leave = new CancellationTokenSource();
        var flagged = new List<bool>();
        page.Rows.CollectionChanged += (_, _) => flagged.Add(page.IsShowingRows);

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;
        await NextReadingAsync(page);

        Assert.NotEmpty(flagged);
        Assert.All(flagged, Assert.True);
        Assert.False(page.IsShowingRows);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>
    /// The tree view fills one level further than the reader can see, which is what puts an expander
    /// on a row, and reads only as far down as the reader has opened.
    /// </summary>
    [Fact]
    public void TheTreeFillsOneLevelPastWhatIsOpenAndFindsOnlyWhatIsOpen() => UiThread.Run(async () =>
    {
        var page = Page(First());
        page.SelectedView = ExploreView.Tree;
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        var tree = page.Tree!;
        var applications = page.Rows.Single(row => row.Key == Applications);
        var alpha = Assert.Single(applications.Children, row => row.Key == Alpha);

        Assert.Empty(alpha.Children);
        Assert.Null(page.Opened(tree.Find(Alpha)!.Value));

        applications.IsExpanded = true;
        page.Open(applications);

        Assert.Same(alpha, page.Opened(tree.Find(Alpha)!.Value));
        Assert.Contains(alpha.Children, row => row.Key == Beta);
        Assert.Null(page.Opened(tree.Find(Beta)!.Value));

        await leave.CancelAsync();
        await watching;
    });

    [Fact]
    public void TheTrailSeparatesEachStepFromTheOneBeforeAndLeadsBack() => UiThread.Run(async () =>
    {
        var page = Page(First());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        page.Descend(page.Tree!.Find(Applications)!.Value);

        Assert.Equal(2, page.Trail.Count);
        Assert.False(page.Trail[0].FollowsAnother);
        Assert.True(page.Trail[1].FollowsAnother);

        page.GoTo(page.Trail[0]);

        Assert.Equal(page.Tree.RootNode, page.CurrentNode);
        Assert.Single(page.Trail);

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>A number from a picture drawn from the reading before names nothing here, and says nothing.</summary>
    [Fact]
    public void ANumberOutsideTheTreeIsLabelledWithNothing() => UiThread.Run(async () =>
    {
        var page = Page(First());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        page.Hover(new ExploreHit(page.Tree!.NodeCount, 0));

        Assert.Equal(string.Empty, page.LabelFor(page.Tree.NodeCount));
        Assert.Equal(string.Empty, page.LabelFor(-1));
        Assert.Equal(string.Empty, page.Hovered);
        Assert.NotEqual(string.Empty, page.LabelFor(page.Tree.RootNode));

        await leave.CancelAsync();
        await watching;
    });

    /// <summary>Windows refused a call the reading is made of, so the page says so and stops.</summary>
    [Fact]
    public void AReadingWindowsRefusesSaysSoAndEndsTheWatch() => UiThread.Run(async () =>
    {
        var page = Page(new QueuedMemorySource(First()) { RefusesFrom = 1 });

        await page.WatchAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.StartsWith("Windows would not answer: ", page.Failure, StringComparison.Ordinal);
        Assert.True(page.HasFailure);
        Assert.Null(page.Tree);
    });

    /// <summary>
    /// §7.2 turns on a reader knowing how old a figure is, so readings that stop for a reason this page
    /// does not name still say they have stopped. The exception itself is not swallowed.
    /// </summary>
    [Fact]
    public void ReadingsThatStopForAnyOtherReasonSaySoAndTheFaultGoesOn() => UiThread.Run(async () =>
    {
        var page = Page(new ScriptedMemorySource(read => read == 1 ? First() : throw new InvalidOperationException("torn")));
        var shown = ShownAsync(page);

        var watching = page.WatchAsync(CancellationToken.None);
        await shown;
        await _clock.WhenWaitingAsync(Patience);
        _clock.Advance(MemoryFeed.Cadence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watching.WaitAsync(Patience));
        Assert.Equal("The readings have stopped. What is on screen is the last one taken.", page.Failure);
        Assert.NotNull(page.Tree);
    });

    [Fact]
    public void LeavingThePageStopsTheReadingsWithoutCallingItAFailure() => UiThread.Run(async () =>
    {
        var page = Page(First());
        using var leave = new CancellationTokenSource();

        var shown = ShownAsync(page);
        var watching = page.WatchAsync(leave.Token);
        await shown;

        await leave.CancelAsync();
        await watching.WaitAsync(Patience);

        Assert.False(page.HasFailure);
    });

    /// <summary>A refusal is reported against the reading that met it, and leaving it up would state it about the next visit.</summary>
    [Fact]
    public void AReturnVisitStartsWithoutTheLastVisitsFailure() => UiThread.Run(async () =>
    {
        var page = Page(new ScriptedMemorySource(read => read == 1 ? throw new Win32Exception(5) : First()));

        await page.WatchAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.True(page.HasFailure);

        var failures = new List<string>();
        page.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(MemoryViewModel.Failure))
            {
                failures.Add(page.Failure);
            }
        };

        using var leave = new CancellationTokenSource();
        var watching = page.WatchAsync(leave.Token);

        Assert.False(page.HasFailure);
        Assert.Equal([string.Empty], failures);

        await leave.CancelAsync();
        await watching.WaitAsync(Patience);
    });
}
