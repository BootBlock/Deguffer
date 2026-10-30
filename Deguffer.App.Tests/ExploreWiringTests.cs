using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page hands what it knows to the parts beside it: when it may act, to what it is
/// pointed at and to the elevation offer; whether the picture on screen is substituted, to the notes;
/// and whether the list is being rewritten, to the page that reads it. Each part decides nothing about
/// these for itself, so a hand-over that is missed leaves a part answering for a page that has moved.
/// </summary>
public sealed class ExploreWiringTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// While a scan runs, neither the scope nor the elevated scan can be asked for, and both buttons
    /// are told so as the scan starts and again as it ends (§7.1).
    /// </summary>
    [Fact]
    public void WhileAScanRunsNeitherTheScopeNorTheElevatedScanCanBeAskedFor() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        page.Targeting.ScopeTo(@"C:\Users\testuser");
        var wholeDrive = page.Targeting.ScanWholeDriveCommand;
        var elevate = page.Elevation.ElevateAndRescanCommand;
        var asked = new List<string>();

        wholeDrive.CanExecuteChanged += (_, _) => asked.Add("whole drive");
        elevate.CanExecuteChanged += (_, _) => asked.Add("elevate");

        Assert.True(wholeDrive.CanExecute(null));
        Assert.True(elevate.CanExecute(null));

        var running = page.ScanCommand.ExecuteAsync(null);

        Assert.False(wholeDrive.CanExecute(null));
        Assert.False(elevate.CanExecute(null));
        Assert.Contains("whole drive", asked);
        Assert.Contains("elevate", asked);

        asked.Clear();
        _explore.Scanner.Current.Finish(ExploreScan.Fast(Folder(@"C:\Users\testuser", "Documents")));
        await running;

        Assert.True(wholeDrive.CanExecute(null));
        Assert.True(elevate.CanExecute(null));
        Assert.Contains("whole drive", asked);
        Assert.Contains("elevate", asked);
    });

    /// <summary>
    /// Pointed somewhere it refuses, the page tells the Scan button and the elevated scan to ask
    /// again, rather than leaving them live beside a status line that has just refused the target.
    /// </summary>
    [Fact]
    public void PointingThePageSomewhereElseAsksBothScanButtonsAgain()
    {
        _explore.Volumes.With(@"C:\").With(@"C:\Cloud\", features: VolumeFeatures.RemoteStorage);
        var page = _explore.Page();
        var asked = new List<string>();

        page.ScanCommand.CanExecuteChanged += (_, _) => asked.Add("scan");
        page.Elevation.ElevateAndRescanCommand.CanExecuteChanged += (_, _) => asked.Add("elevate");

        page.Targeting.ScopeTo(@"C:\Cloud\Photos");

        Assert.False(page.ScanCommand.CanExecute(null));
        Assert.False(page.Elevation.ElevateAndRescanCommand.CanExecute(null));
        Assert.Contains("scan", asked);
        Assert.Contains("elevate", asked);
    }

    /// <summary>
    /// A running scan's snapshot is not the picture asked for, and the notes say so as it arrives.
    /// The finished scan takes the note away again.
    /// </summary>
    [Fact]
    public void TheNotesSayWhileTheSnapshotOnScreenIsNotThePictureAskedFor() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        page.SelectedView = ExploreView.Files;

        var running = page.ScanCommand.ExecuteAsync(null);
        var snapshot = Folder(@"C:\", "Users", ExploreChildOrder.ByName);

        _explore.Scanner.Current.Report(new ExploreProgress(1, null, 0, snapshot));
        await Task.Yield();

        Assert.True(page.HasViewNote);
        Assert.True(page.Notes.HasViewNote);
        Assert.True(page.Notes.ShowsPanel);

        _explore.Scanner.Current.Finish(ExploreScan.Fast(Folder(@"C:\", "Users")));
        await running;

        Assert.False(page.HasViewNote);
        Assert.False(page.Notes.HasViewNote);
    });

    /// <summary>
    /// The page is told the folder list is being rewritten for every change the rewrite makes to it,
    /// and only then, so the list's own selection changes during it are not taken for a gesture.
    /// </summary>
    [Fact]
    public void TheListIsMarkedAsRewrittenForEveryChangeTheRewriteMakes() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var during = new List<bool>();

        page.Listing.Rows.CollectionChanged += (_, _) => during.Add(page.IsShowingRows);

        await _explore.ScanAsync(page, ExploreScan.Fast(Folder(@"C:\", "Users")));

        Assert.NotEmpty(during);
        Assert.All(during, Assert.True);
        Assert.False(page.IsShowingRows);
    });

    private static ExploreTree Folder(string root, string child, ExploreChildOrder order = ExploreChildOrder.BySize)
    {
        var builder = new ExploreTreeBuilder(root);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder(child)]);

        return builder.Build(order);
    }
}
