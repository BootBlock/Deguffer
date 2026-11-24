using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage rows waiting for a source folder offer to add one where they stand (§7), and a folder
/// added that way is approved as Settings approves one and plans those rows again in place. Which rows
/// are planned again is Core's, and proven in <c>SourceFolderChangesTests</c>; this proves the page
/// hands it every row it shows and plans exactly what it answers.
/// </summary>
public sealed class CleanViewModelSourceFolderTests
{
    /// <summary>Only a row the scan found waiting for a folder offers to add one.</summary>
    [Fact]
    public void OnlyARowWaitingForAFolderOffersToAddOne()
    {
        FakeCleanupProvider[] providers =
        [
            new("builds") { IsPresent = false, IsAwaitingSourceFolders = true },
            new("missing") { IsPresent = false },
            new("ready") { Steps = [Rows.Folder("r", 10)] },
        ];
        using var page = new StoragePage(providers);

        page.Scan();

        Assert.True(page.Row("builds").OffersSourceFolder);
        Assert.False(page.Row("missing").OffersSourceFolder);
        Assert.False(page.Row("ready").OffersSourceFolder);
    }

    /// <summary>
    /// The folder is stored, and every row that was waiting is planned again, once, while a row that
    /// was not is left as it stands. The rows leave the folded group for the list with their new
    /// figures, and Clean is offered again over a list that describes the whole disk.
    /// </summary>
    [Fact]
    public void AFolderAddedFromTheRowsIsApprovedAndPlansEveryWaitingRowAgainAndNoOther()
    {
        var builds = new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true };
        var dotnet = new FakeCleanupProvider("dotnet") { IsAwaitingSourceFolders = true };
        var npm = new FakeCleanupProvider("npm") { Steps = [Rows.Folder("npm", 10)] };
        using var page = new StoragePage([builds, dotnet, npm]);
        page.Scan();

        // What each provider answers once a folder is approved. The real ones read the stored list.
        builds.IsPresent = true;
        builds.IsAwaitingSourceFolders = false;
        builds.Steps = [Rows.Folder("node_modules", 4096)];
        dotnet.IsAwaitingSourceFolders = false;

        var folder = Path.Combine(page.Environment.UserProfile, "src");
        page.SourceFolderPrompt.Picked = folder;

        page.AddSourceFolder();

        Assert.Equal([folder], page.SourceRoots.Current.Select(root => root.Path));
        Assert.Equal([folder], new SourceRootStore(page.Environment).Load().Select(root => root.Path));
        Assert.Equal(2, builds.PlanCount);
        Assert.Equal(2, dotnet.PlanCount);
        Assert.Equal(1, npm.PlanCount);
        Assert.Equal(PreviewPlace.Listed, page.Row("builds").Place);
        Assert.False(page.Row("builds").OffersSourceFolder);
        Assert.Contains(page.Row("builds"), page.ViewModel.Listed);
        Assert.False(page.ViewModel.AwaitingFolders.HasRows);
        Assert.True(page.ViewModel.HasPreview);
        Assert.False(page.ViewModel.IsBusy);
    }

    /// <summary>
    /// A picker closed without a choice, a choice that is not a folder on a disk and a save that
    /// failed all store nothing, so nothing is planned again. The two the user did not choose are
    /// said on the bar, the failed save as an error.
    /// </summary>
    [Fact]
    public void AChoiceThatStoresNothingPlansNothing()
    {
        var builds = new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true };
        using var page = new StoragePage([builds]);
        page.Scan();
        var said = page.ViewModel.Status;

        page.SourceFolderPrompt.Picked = null;
        page.AddSourceFolder();

        Assert.Equal(said, page.ViewModel.Status);

        page.SourceFolderPrompt.Picked = string.Empty;
        page.AddSourceFolder();

        Assert.Equal(PickedFolder.NotOnDisk, page.ViewModel.Status);

        page.SourceFolderPrompt.Picked = Path.Combine(page.Environment.UserProfile, "src");
        Directory.CreateDirectory(Path.Combine(page.Environment.LocalAppData, "Deguffer", "source-roots.json"));
        page.AddSourceFolder();

        Assert.Equal(SettingSave.NoticeMessage, page.ViewModel.Status);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
        Assert.Empty(page.SourceRoots.Current);
        Assert.Equal(1, builds.PlanCount);
        Assert.True(page.ViewModel.HasPreview);
    }

    /// <summary>A folder on cloud storage is added from the rows only once its warning is accepted, as from Settings.</summary>
    [Fact]
    public void AFolderOnCloudStorageIsAskedAboutAndDecliningStoresNothing()
    {
        var builds = new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true };
        using var page = new StoragePage([builds]);
        page.Volumes.With(@"V:\", features: VolumeFeatures.RemoteStorage);
        page.Scan();

        page.SourceFolderPrompt.Picked = @"V:\work";
        page.SourceFolderPrompt.Accepts = false;
        page.AddSourceFolder();

        Assert.Equal(SourceRootApproval.RemoteStorageWarning, Assert.Single(page.SourceFolderPrompt.Asked).Warning);
        Assert.Empty(page.SourceRoots.Current);
        Assert.Equal(1, builds.PlanCount);
    }

    /// <summary>
    /// Cancel stops the search the folder started. The folder stays approved, the rows stay withdrawn
    /// as any cancelled re-plan leaves them, and the bar says how to see the rest.
    /// </summary>
    [Fact]
    public void CancelStopsTheSearchAndLeavesTheFolderApproved()
    {
        var builds = new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true };
        using var page = new StoragePage([builds]);
        page.Scan();

        builds.AwaitWhilePlanning = async ct =>
        {
            page.ViewModel.CancelCommand.Execute(null);
            await Task.Delay(Timeout.Infinite, ct);
        };
        page.SourceFolderPrompt.Picked = Path.Combine(page.Environment.UserProfile, "src");

        page.AddSourceFolder();

        Assert.Single(page.SourceRoots.Current);
        Assert.False(page.ViewModel.HasPreview);
        Assert.False(page.ViewModel.IsBusy);
        Assert.Equal(InfoBarSeverity.Warning, page.ViewModel.StatusSeverity);
        Assert.Contains("Scan to see everything it holds", page.ViewModel.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A search the folder started that fails is reported as an error rather than taking the window
    /// down, as a scan's failure is, and marks the page's work as failed. The folder stays approved.
    /// </summary>
    [Fact]
    public void ASearchThatFailsIsReportedAndLeavesTheFolderApproved()
    {
        var builds = new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true };
        using var page = new StoragePage([builds]);
        page.Scan();
        var failures = 0;
        page.Work.Failed += (_, _) => failures++;

        builds.PlanFailure = new IOException("The device is not ready.");
        page.SourceFolderPrompt.Picked = Path.Combine(page.Environment.UserProfile, "src");

        page.AddSourceFolder();

        Assert.Single(page.SourceRoots.Current);
        Assert.Equal(InfoBarSeverity.Error, page.ViewModel.StatusSeverity);
        Assert.StartsWith("The folder is approved, but Deguffer could not finish searching it", page.ViewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, failures);
        Assert.False(page.ViewModel.IsBusy);
        Assert.True(page.Work.IsIdle);
    }

    /// <summary>
    /// The rows cannot ask for a folder while the page is busy, as Scan cannot start, nor over a
    /// preview a cancelled scan or re-plan left part-way: the re-plan would put Clean back over plans
    /// no scan stands behind.
    /// </summary>
    [Fact]
    public void TheActionIsOffWhileThePageIsBusyOrHasNoWholePreview()
    {
        using var page = new StoragePage([new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true }]);
        var command = page.ViewModel.AddSourceFolderCommand;

        Assert.False(command.CanExecute(null));

        page.Scan();

        Assert.True(command.CanExecute(null));

        page.ViewModel.IsBusy = true;

        Assert.False(command.CanExecute(null));

        page.ViewModel.IsBusy = false;
        page.ViewModel.HasPreview = false;

        Assert.False(command.CanExecute(null));
    }

    /// <summary>
    /// The page is busy while the folder is asked for, so no scan or clean can start in the wait for
    /// the volumes before the warning and then run under the re-plan.
    /// </summary>
    [Fact]
    public void ThePageIsBusyWhileTheFolderIsAskedFor()
    {
        using var page = new StoragePage([new FakeCleanupProvider("builds") { IsPresent = false, IsAwaitingSourceFolders = true }]);
        page.Scan();
        bool? scanCouldStart = null;
        page.SourceFolderPrompt.WhilePicking = () => scanCouldStart = page.ViewModel.PreviewCommand.CanExecute(null);

        page.AddSourceFolder();

        Assert.False(scanCouldStart);
        Assert.False(page.ViewModel.IsBusy);
        Assert.True(page.ViewModel.PreviewCommand.CanExecute(null));
    }
}
