using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The "When complete" box on the Storage page: what it lists and remembers, and how the page carries
/// the choice out once a clean has finished. Which runs are followed is Core's rule; these tests hold
/// the page to calling it, to counting down first, and to doing nothing it was not asked to.
/// </summary>
public sealed class WhenCompleteTests
{
    [Fact]
    public void AFinishedCleanCountsDownThenCarriesOutTheChoice()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var taken = page.Cache("taken", 1024);
        cache.Steps = [taken];
        page.Scan();
        var countdowns = Answer(page, true);
        Choose(page, CompletionAction.ShutDown);

        page.Clean();

        Assert.Equal([CompletionAction.ShutDown], countdowns);
        Assert.Equal([CompletionAction.ShutDown], page.Session.Performed);

        // The choice changes what follows a clean and nothing about what it takes (§5.6).
        Assert.False(Directory.Exists(taken.Path));
        Assert.True(Directory.Exists(page.ToolRoot));
    }

    [Fact]
    public void ACancelledCountdownLeavesTheMachineAlone()
    {
        using var page = Cleanable();
        var countdowns = Answer(page, false);
        Choose(page, CompletionAction.Restart);

        page.Clean();

        Assert.Equal([CompletionAction.Restart], countdowns);
        Assert.Empty(page.Session.Performed);
    }

    [Fact]
    public void DoNothingAsksNothing()
    {
        using var page = Cleanable();
        var countdowns = Answer(page, true);

        page.Clean();

        Assert.Equal(CompletionAction.Nothing, page.ViewModel.WhenComplete.Selected);
        Assert.Empty(countdowns);
        Assert.Empty(page.Session.Performed);
    }

    /// <summary>The verdict must be read before the next run, and a machine that is off cannot show it.</summary>
    [Fact]
    public void AVerificationFailureIsNotFollowed()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var tree = page.Cache("tree", 1024);
        cache.Steps = [tree];
        cache.ProtectedPaths = [new ProtectedPath(Path.Combine(tree.Path, "entry.bin"), "Must survive.", PathPresence.Present)];
        page.Scan();
        var countdowns = Answer(page, true);
        Choose(page, CompletionAction.ShutDown);

        page.Clean();

        Assert.True(page.ViewModel.RunVerificationFailed);
        Assert.Empty(countdowns);
        Assert.Empty(page.Session.Performed);
    }

    /// <summary>Whoever cancelled the clean is at the machine.</summary>
    [Fact]
    public void ACancelledCleanIsNotFollowed()
    {
        var first = new FakeCleanupProvider("first");
        var second = new FakeCleanupProvider("second");
        using var page = new StoragePage([first, second]);
        first.Steps = [page.Cache("first", 1024)];
        second.Steps = [page.Cache("second", 1024)];
        first.AfterCleaning = () => page.ViewModel.CancelCommand.Execute(null);
        page.Scan();
        var countdowns = Answer(page, true);
        Choose(page, CompletionAction.LogOff);

        page.Clean();

        Assert.Empty(countdowns);
        Assert.Empty(page.Session.Performed);
    }

    /// <summary>
    /// Cancel is still on screen while the rows the run changed are planned again, and pressing it
    /// there interrupts no deletion, so the run's own outcome is not a cancelled one.
    /// </summary>
    [Fact]
    public void ACancelPressedWhileTheRowsArePlannedAgainIsNotFollowed()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("taken", 1024)];
        cache.AfterCleaning = () => cache.WhilePlanning = () => page.ViewModel.CancelCommand.Execute(null);
        page.Scan();
        var countdowns = Answer(page, true);
        Choose(page, CompletionAction.ShutDown);

        page.Clean();

        Assert.True(page.ViewModel.HasRunResult);
        Assert.Empty(countdowns);
        Assert.Empty(page.Session.Performed);
    }

    /// <summary>A clean nobody confirmed never ran, so there is nothing to follow.</summary>
    [Fact]
    public void ADeclinedCleanIsNotFollowed()
    {
        var sdk = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost);
        using var page = new StoragePage([sdk]);
        sdk.Steps = [page.Cache("sdk", 4096)];
        page.Scan();
        page.Row("sdk").IsSelected = true;
        page.Prompt.Agrees = false;
        var countdowns = Answer(page, true);
        Choose(page, CompletionAction.ShutDown);

        page.Clean();

        Assert.Empty(sdk.Executed);
        Assert.Empty(countdowns);
        Assert.Empty(page.Session.Performed);
    }

    /// <summary>Choosing to shut down part-way through a long clean is the ordinary way to use the box.</summary>
    [Fact]
    public void TheChoiceIsReadWhenTheCleanEnds()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("taken", 1024)];
        cache.AfterCleaning = () => Choose(page, CompletionAction.Lock);
        page.Scan();
        Answer(page, true);

        page.Clean();

        Assert.Equal([CompletionAction.Lock], page.Session.Performed);
    }

    /// <summary>Closing Deguffer is the window's to do, through the same guard as its close button.</summary>
    [Fact]
    public void ExitAsksTheWindowAndNotTheSession()
    {
        using var page = Cleanable();
        Answer(page, true);
        Choose(page, CompletionAction.ExitDeguffer);
        var exits = 0;
        page.ViewModel.WhenComplete.ExitRequested += (_, _) => exits++;

        page.Clean();

        Assert.Equal(1, exits);
        Assert.Empty(page.Session.Performed);
    }

    [Fact]
    public void ARefusalIsReportedWithWindowsReason()
    {
        using var page = Cleanable();
        Answer(page, true);
        Choose(page, CompletionAction.Hibernate);
        page.Session.Refusal = "The request is not supported.";

        page.Clean();

        Assert.Equal(
            "The clean finished, but Deguffer could not hibernate this PC. The request is not supported.",
            page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
    }

    /// <summary>A view that never supplies a countdown has no way to warn anybody, so nothing is carried out.</summary>
    [Fact]
    public void NothingIsCarriedOutWithoutACountdown()
    {
        using var page = Cleanable();
        Choose(page, CompletionAction.ShutDown);

        page.Clean();

        Assert.Empty(page.Session.Performed);
    }

    [Fact]
    public void TheChoiceIsRememberedForTheNextLaunch()
    {
        using var page = Cleanable();

        Choose(page, CompletionAction.Restart);

        Assert.Equal(CompletionAction.Restart, page.Preferences.Current.WhenCleanComplete);
        Assert.Equal(
            CompletionAction.Restart,
            new WhenCompleteViewModel(new PreferenceService(new PreferenceStore(page.Environment)), page.Session).Selected);
    }

    /// <summary>
    /// A stored choice this machine cannot carry out is shown as Do nothing, and left on disk so that
    /// switching hibernation back on brings it back.
    /// </summary>
    [Fact]
    public void AChoiceTheMachineCannotMakeShowsAsDoNothingAndStaysStored()
    {
        using var page = Cleanable();
        Choose(page, CompletionAction.Hibernate);
        var preferences = new PreferenceService(new PreferenceStore(page.Environment));

        var box = new WhenCompleteViewModel(preferences, new FakeWindowsSession { CanHibernate = false });

        Assert.Equal(CompletionAction.Nothing, box.Selected);
        Assert.DoesNotContain("Hibernate", box.Labels);
        Assert.Equal(CompletionAction.Hibernate, preferences.Current.WhenCleanComplete);
    }

    [Fact]
    public void TheBoxListsWhatTheMachineOffersByName()
    {
        using var page = Cleanable();

        Assert.Equal(
            ["Do nothing", "Exit Deguffer", "Lock PC", "Log off", "Sleep", "Hibernate", "Restart PC", "Shut down PC"],
            page.ViewModel.WhenComplete.Labels);
    }

    private static StoragePage Cleanable()
    {
        var cache = new FakeCleanupProvider("cache");
        var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("taken", 1024)];
        page.Scan();

        return page;
    }

    private static void Choose(StoragePage page, CompletionAction action) =>
        page.ViewModel.WhenComplete.SelectedIndex = page.ViewModel.WhenComplete.Labels
            .ToList()
            .IndexOf(WhenCleanComplete.Label(action));

    /// <summary>Answer every countdown with <paramref name="due"/>, and return the actions counted down to.</summary>
    private static List<CompletionAction> Answer(StoragePage page, bool due)
    {
        var countdowns = new List<CompletionAction>();
        page.ViewModel.WhenComplete.ConfirmAsync = countdown =>
        {
            countdowns.Add(countdown.Action);
            return Task.FromResult(due);
        };

        return countdowns;
    }
}
