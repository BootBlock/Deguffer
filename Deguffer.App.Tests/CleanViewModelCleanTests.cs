using System.Collections.Specialized;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's clean: which rows and steps it hands the planner, what it asks first, what it
/// reports afterwards, and how the rows it changed are planned again where they stand.
/// </summary>
public class CleanViewModelCleanTests
{
    /// <summary>
    /// §5.6's negative, from the page: a clean takes the ticked steps and nothing else. The unticked
    /// sibling, the tool's own folder (§5.2) and the file the plan protects are all still there.
    /// </summary>
    [Fact]
    public void ACleanTakesWhatIsTickedAndLeavesEverythingElseStanding()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var taken = page.Cache("taken", 1024);
        var unticked = page.Cache("unticked", 2048);
        var config = Path.Combine(page.ToolRoot, "config.json");
        File.WriteAllText(config, "{}");
        cache.Steps = [taken, unticked];
        cache.ProtectedPaths = [new ProtectedPath(config, "The tool's settings.", PathPresence.Present)];
        page.Scan();

        page.Row("cache").Steps[1].IsSelected = false;
        page.Clean();

        Assert.False(Directory.Exists(taken.Path));
        Assert.True(Directory.Exists(unticked.Path));
        Assert.True(Directory.Exists(page.ToolRoot));
        Assert.True(File.Exists(config));

        var ran = Assert.Single(cache.Executed);
        Assert.Equal([taken.Path], ran.Steps.Select(step => step.SelectionKey));
        Assert.Contains(ran.ProtectedPaths, p => p.Path == unticked.Path);
        Assert.Equal("All protected paths survived.", page.ViewModel.RunStatement);
        Assert.False(page.ViewModel.RunVerificationFailed);
        Assert.Equal("1 KB", page.ViewModel.RemovedLabel);
    }

    /// <summary>
    /// §5.4 and §7: what the run removed and how the volume's free space moved are two figures, and the
    /// second is read from the volume either side of the run.
    /// </summary>
    [Fact]
    public void TheFreeSpaceChangeIsReadFromTheVolumeEitherSideOfTheRun()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache], freeBytes: 400_000);
        cache.Steps = [page.Cache("a", 1024)];
        cache.AfterCleaning = () => page.FreeSpaceBecomes(410_240);
        page.Scan();

        page.Clean();

        Assert.Equal(FreeSpace.Format(10_240), page.ViewModel.FreeSpaceChangeLabel);
        Assert.Equal(410_240, page.ViewModel.FreeSpaceNow);
        Assert.True(page.ViewModel.HasRunResult);
    }

    /// <summary>A volume that will not say is a dash, rather than a change worked out from one reading.</summary>
    [Fact]
    public void AVolumeThatStopsAnsweringLeavesTheChangeUnstated()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        cache.AfterCleaning = () => page.Volumes.Without(page.VolumeRoot);
        page.Scan();

        page.Clean();

        Assert.Equal("—", page.ViewModel.FreeSpaceChangeLabel);
        Assert.Null(page.ViewModel.FreeSpaceNow);
    }

    /// <summary>
    /// Declining one row's confirmation drops that row and runs the rest. The dialog is built once, and
    /// only because something needed asking.
    /// </summary>
    [Fact]
    public void ADeclinedRowIsDroppedAndTheRestRun()
    {
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([sdk, cache]);
        var sdkFolder = page.Cache("sdk", 4096);
        sdk.Steps = [sdkFolder];
        cache.Steps = [page.Cache("npm", 1024)];
        page.Scan();
        page.Row("sdk").IsSelected = true;
        page.Prompt.Agrees = false;

        page.Clean();

        Assert.Empty(sdk.Executed);
        Assert.Single(cache.Executed);
        Assert.True(Directory.Exists(sdkFolder.Path));
        Assert.Equal(1, page.PromptsBuilt);
        Assert.Equal(["sdk"], page.Prompt.Asked.Select(r => r.ProviderId));
    }

    /// <summary>A run of Tier 1 alone asks nothing, so it never builds a dialog.</summary>
    [Fact]
    public void ARunNeedingNoConfirmationBuildsNoDialog()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        page.Scan();

        page.Clean();

        Assert.Single(cache.Executed);
        Assert.Equal(0, page.PromptsBuilt);
    }

    /// <summary>
    /// A run the user stopped says so, as a warning, and says it was their refusal rather than an
    /// empty selection.
    /// </summary>
    [Fact]
    public void DecliningEveryRowSaysNothingWasConfirmed()
    {
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([sdk]);
        sdk.Steps = [page.Cache("sdk", 4096)];
        page.Scan();
        page.Row("sdk").IsSelected = true;
        page.Prompt.Agrees = false;

        page.Clean();

        Assert.Empty(sdk.Executed);
        Assert.Equal("Nothing was cleaned — no selected item was confirmed.", page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Warning, page.ViewModel.StatusSeverity);
        Assert.False(page.ViewModel.IsBusy);
    }

    /// <summary>Reporting a refusal to someone who was never asked would describe the wrong event.</summary>
    [Fact]
    public void ACleanWithNothingTickedSaysThereWasNothingToRemove()
    {
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([sdk]);
        sdk.Steps = [page.Cache("sdk", 4096)];
        page.Scan();

        page.Clean();

        Assert.Equal("Nothing was cleaned — the selected items had nothing to remove.", page.ViewModel.Status);
        Assert.Empty(page.Prompt.Asked);
    }

    /// <summary>
    /// Declining the blanket confirmation leaves the screen exactly as it was: nothing runs, the page
    /// never goes busy, and the previous run's figures are not cleared.
    /// </summary>
    [Fact]
    public void DecliningTheBlanketConfirmationLeavesTheScreenAsItWas()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        page.Scan();
        var status = page.ViewModel.Status;
        var asked = new List<CleanConfirmation>();
        page.ViewModel.ConfirmCleanAsync = confirmation =>
        {
            asked.Add(confirmation);
            return Task.FromResult(false);
        };
        var raised = page.ViewModel.Notifications();

        page.Clean();

        Assert.Single(asked);
        Assert.Empty(cache.Executed);
        Assert.Equal(status, page.ViewModel.Status);
        Assert.DoesNotContain(nameof(CleanViewModel.IsBusy), raised);
    }

    /// <summary>
    /// The rows a run changed are planned again and written over where they stand: the list is told
    /// nothing, so the reader keeps their place. A row the run did not reach is not measured again.
    /// </summary>
    [Fact]
    public void TheRowsARunChangedArePlannedAgainWhereTheyStand()
    {
        var cache = new FakeCleanupProvider("cache");
        var other = new FakeCleanupProvider("other");
        using var page = new StoragePage([cache, other]);
        var a = page.Cache("a", 1024);
        var b = page.Cache("b", 2048);
        cache.Steps = [a, b];
        other.Steps = [Rows.Folder("elsewhere", 10)];
        page.Scan();
        page.Row("other").IsSelected = false;
        page.Row("cache").Steps[1].IsSelected = false;
        var row = page.Row("cache");
        cache.AfterCleaning = () => cache.Steps = [b];
        var changes = new List<NotifyCollectionChangedAction>();
        page.ViewModel.Findings.CollectionChanged += (_, e) => changes.Add(e.Action);
        var otherPlans = other.PlanCount;

        page.Clean();

        Assert.Same(row, page.Row("cache"));
        Assert.Empty(changes);
        Assert.Equal(otherPlans, other.PlanCount);
        Assert.Single(row.Steps);
        Assert.False(row.IsSelected);
        Assert.Equal("0 B", page.ViewModel.SelectedTotalLabel);
        Assert.True(page.ViewModel.HasPreview);
    }

    /// <summary>
    /// The bar moves in steps of half a percent at least, and always reaches the end. Every report
    /// that reaches the property re-lays out the bar, and a removal reports every 256 files.
    /// </summary>
    [Fact]
    public void TheProgressBarMovesOnlyByAVisibleAmountAndReachesTheEnd()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        cache.Fractions = [0.001, 0.002, 0.5, 0.501, 0.504];
        page.Scan();
        var seen = new List<double>();
        page.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanViewModel.CleanPercent))
            {
                seen.Add(page.ViewModel.CleanPercent);
            }
        };

        page.Clean();

        Assert.Equal([50.0, 100.0], seen);
        Assert.False(page.ViewModel.IsCleaning);
    }

    /// <summary>
    /// A protected path the run took is an alarm, and it keeps the bar as an error with every check
    /// listed under it. Ticking a row afterwards must not replace it with the scan's sentence.
    /// </summary>
    [Fact]
    public void AVerificationFailureKeepsTheBarThroughLaterTicking()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var tree = page.Cache("tree", 1024);
        var inside = Path.Combine(tree.Path, "entry.bin");
        var other = page.Cache("other", 1024);
        cache.Steps = [tree, other];
        cache.ProtectedPaths = [new ProtectedPath(inside, "Must survive.", PathPresence.Present)];
        cache.AfterCleaning = () => (cache.Steps, cache.ProtectedPaths) = ([other], []);
        page.Scan();
        page.Row("cache").Steps[1].IsSelected = false;

        page.Clean();

        Assert.True(page.ViewModel.RunVerificationFailed);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
        Assert.Equal(page.ViewModel.RunStatement, page.ViewModel.Status);
        Assert.Contains(page.ViewModel.RunVerificationNotes, check => check.Subject == inside);

        Assert.Equal("0 B", page.ViewModel.SelectedTotalLabel);
        page.Row("cache").Steps[0].IsSelected = true;

        Assert.Equal("1 KB", page.ViewModel.SelectedTotalLabel);
        Assert.Equal(page.ViewModel.RunStatement, page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
    }

    /// <summary>
    /// A run whose checks all passed yields the bar to the fresh scan's sentence, which then follows
    /// the ticking as it did before the run.
    /// </summary>
    [Fact]
    public void ARunThatPassedHandsTheBarBackToTheScansSentence()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var b = page.Cache("b", 2048);
        cache.Steps = [page.Cache("a", 1024), b];
        cache.AfterCleaning = () => cache.Steps = [b];
        page.Scan();
        page.Row("cache").Steps[1].IsSelected = false;

        page.Clean();

        Assert.Equal("2 KB can be reclaimed, 0 B selected. Tick the rows you want, then Clean.", page.ViewModel.Status);

        page.Row("cache").Steps[0].IsSelected = true;

        Assert.Equal("2 KB can be reclaimed, 2 KB selected. Review the rows, then Clean.", page.ViewModel.Status);
    }
}
