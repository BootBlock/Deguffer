using System.Collections.Specialized;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page holds Core's decisions together: the order it is built in, where each way
/// of re-pointing it leaves the drive box and the folder, what a scan's snapshots do to the rows and
/// the selection, and which notes and controls are on screen when.
/// </summary>
public sealed class ExploreViewModelTests : IDisposable
{
    private const string ScanPrompt = "Choose a drive or a folder and scan it to see what is using the space.";

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The page is built with nothing but its seams: no window, no XAML runtime, no real volumes.
    /// It opens on the first drive Explore will scan, with nothing refused, and offers an elevated
    /// scan before anything has been scanned.
    /// </summary>
    [Fact]
    public void OpensOnTheFirstDriveItWillScanWithTheElevatedScanOffered()
    {
        _explore.Volumes
            .With(@"A:\", features: VolumeFeatures.RemoteStorage)
            .With(@"C:\")
            .With(@"D:\");

        var page = _explore.Page();

        Assert.Equal(@"C:\", page.SelectedDrive?.RootPath);
        Assert.Null(page.ScopeFolder);
        Assert.Equal(ScanPrompt, page.Status);
        Assert.True(page.ScanCommand.CanExecute(null));
        Assert.True(page.CanElevate);
        Assert.Equal(ElevationOffer.Label(hasScanned: false), page.ElevateLabel);
        Assert.NotEmpty(page.AgeLegend);
        Assert.Empty(_explore.Relaunches);
    }

    [Fact]
    public void AnElevatedPageOffersNoElevation()
    {
        _explore.Volumes.With(@"C:\");

        Assert.False(_explore.Page(isElevated: true).CanElevate);
    }

    /// <summary>
    /// Where every volume is refused, the page opens on one and says why it will not scan it, rather
    /// than opening with a dead button and nothing said.
    /// </summary>
    [Fact]
    public void WhereEveryDriveIsRefusedThePageOpensSayingWhy()
    {
        _explore.Volumes.With(@"G:\", features: VolumeFeatures.RemoteStorage);

        var page = _explore.Page();

        Assert.Equal(@"G:\", page.SelectedDrive?.RootPath);
        Assert.Equal(DriveChoice.RemoteStorageRefusal, page.Status);
        Assert.False(page.ScanCommand.CanExecute(null));
    }

    /// <summary>
    /// A removal policy that is already built when the page is made announces itself inside the
    /// selection's constructor, before the page has subscribed to anything. Nothing may be lost by
    /// that: the first selection is still answered by the policy rather than by "in a moment", and
    /// the notes still come up to say so.
    /// </summary>
    [Fact]
    public void APolicyBuiltBeforeThePageSubscribesStillAnswersTheFirstSelection() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        _explore.Build = _ => Task.FromResult(_explore.Policy("Kept for a reason.", @"C:\kept"));

        var page = _explore.Page();
        var tree = Drive(ExploreFixture.Folder("kept"), ExploreFixture.Folder("loose"));

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.Selection.Select([Child(tree, "kept")]);

        Assert.Equal("Kept for a reason.", page.Selection.Note);
        Assert.True(page.ShowsNotes);
    });

    /// <summary>
    /// Choosing a drive in the picker drops the folder, and puts the elevation offer back to what it
    /// says before anything is scanned: it describes what the button would scan now.
    /// </summary>
    [Fact]
    public void PickingADriveDropsTheFolderAndPutsTheOfferBack() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\").With(@"D:\");
        var page = _explore.Page();

        page.ScopeTo(@"C:\Users\testuser");
        await _explore.ScanAsync(page, ExploreScan.Fast(new ExploreTreeBuilder(@"C:\Users\testuser").Build(ExploreChildOrder.BySize)));

        Assert.False(page.CanElevate);
        Assert.Equal(ElevationOffer.Label(hasScanned: true), page.ElevateLabel);

        page.SelectedDrive = page.Drives.Single(drive => drive.RootPath == @"D:\");

        Assert.Null(page.ScopeFolder);
        Assert.True(page.CanElevate);
        Assert.Equal(ElevationOffer.Label(hasScanned: false), page.ElevateLabel);
    });

    [Fact]
    public void ScopingToAFolderMovesTheDriveBoxToTheVolumeHoldingIt()
    {
        _explore.Volumes.With(@"C:\").With(@"D:\");
        var page = _explore.Page();

        page.ScopeTo(@"D:\Photos");

        Assert.Equal(@"D:\", page.SelectedDrive?.RootPath);
        Assert.Equal(@"D:\Photos", page.ScopeFolder);
        Assert.True(page.IsScopedToFolder);
    }

    /// <summary>
    /// A folder on cloud storage is refused on the status line, and the refusal is taken back once
    /// the page points somewhere it will scan — its own sentence, and nobody else's.
    /// </summary>
    [Fact]
    public void ARefusalIsStatedAndTakenBackButNothingElseIs()
    {
        _explore.Volumes.With(@"C:\").With(@"C:\Cloud\", features: VolumeFeatures.RemoteStorage);
        var page = _explore.Page();

        page.ScopeTo(@"C:\Cloud\Photos");

        Assert.Equal(DriveChoice.RemoteStorageRefusal, page.Status);
        Assert.False(page.ScanCommand.CanExecute(null));

        page.ScopeTo(@"C:\Users\testuser");

        Assert.Equal(ScanPrompt, page.Status);
        Assert.True(page.ScanCommand.CanExecute(null));

        page.ScopeTo(@"C:\Cloud\Photos");
        page.Status = "Something another part of the page said.";
        page.ScopeTo(@"C:\Users\testuser");

        Assert.Equal("Something another part of the page said.", page.Status);
    }

    /// <summary>
    /// An elevated replacement is pointed where the page it replaced was. A drive that has gone, with
    /// no folder beside it, restores nothing, and the page must not scan its default on the strength
    /// of it.
    /// </summary>
    [Fact]
    public void PointingAtAGoneDriveRestoresNothing()
    {
        _explore.Volumes.With(@"C:\").With(@"D:\");
        var page = _explore.Page();

        Assert.False(page.PointAt(@"E:\", null));
        Assert.Equal(@"C:\", page.SelectedDrive?.RootPath);

        Assert.True(page.PointAt(@"D:\", @"D:\Photos"));
        Assert.Equal(@"D:\", page.SelectedDrive?.RootPath);
        Assert.Equal(@"D:\Photos", page.ScopeFolder);
    }

    /// <summary>
    /// A drive taken away between two readings moves the page to one nobody picked, and a folder on
    /// the drive that went goes with it.
    /// </summary>
    [Fact]
    public void AReadingThatLosesTheChosenDriveDropsTheFolder()
    {
        _explore.Volumes.With(@"C:\").With(@"E:\");
        var page = _explore.Page();

        page.ScopeTo(@"E:\Photos");
        _explore.Volumes.Without(@"E:\");
        _explore.Time.Advance(DriveList.FreshFor);
        page.RefreshDrives();

        Assert.Equal(@"C:\", page.SelectedDrive?.RootPath);
        Assert.Null(page.ScopeFolder);
    }

    /// <summary>
    /// The picker lets go of its selection while the list under it changes, writing null back
    /// through its binding. That is not the reader choosing no drive, so a reading that hands the
    /// chosen drive back keeps the folder and the offer the last scan made.
    /// </summary>
    [Fact]
    public void AReadingThatHandsTheDriveBackKeepsTheFolderAndTheOffer() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        page.ScopeTo(@"C:\Users\testuser");
        await _explore.ScanAsync(page, ExploreScan.Fast(new ExploreTreeBuilder(@"C:\Users\testuser").Build(ExploreChildOrder.BySize)));

        // What a ComboBox does when the collection under its selected item changes.
        page.Drives.CollectionChanged += (_, _) => page.SelectedDrive = null;

        _explore.Volumes.With(@"E:\");
        _explore.Time.Advance(DriveList.FreshFor);
        page.RefreshDrives();

        Assert.Equal(@"C:\", page.SelectedDrive?.RootPath);
        Assert.Equal(@"C:\Users\testuser", page.ScopeFolder);
        Assert.False(page.CanElevate);
        Assert.Equal(ElevationOffer.Label(hasScanned: true), page.ElevateLabel);
    });

    /// <summary>§7.1: a total that is a lower bound says so, on the status line as well as on the row.</summary>
    [Fact]
    public void AFinishedScanSaysWhetherItsTotalIsALowerBound() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var builder = new ExploreTreeBuilder(@"C:\");
        var refused = builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("refused")]);

        await _explore.ScanAsync(page, ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));

        Assert.Equal(@"C:\", _explore.Scanner.Current.Root);
        Assert.Equal($"{FreeSpace.Format(0)} accounted for.", page.Status);

        builder.MarkSizeUnknown(refused);
        await _explore.ScanAsync(page, ExploreScan.Walked(builder.Build(ExploreChildOrder.BySize), FallbackReason.NotElevated));

        Assert.EndsWith("Some of this drive could not be read, so the totals are lower bounds.", page.Status);
        Assert.True(page.HasRouteNote);
        Assert.True(page.CanElevate);
    });

    /// <summary>
    /// A snapshot is the same directory measured again. What is picked stays picked, and the rows
    /// are brought up to date in place, so the reader keeps their place in the list while the scan
    /// runs. The finished tree is in a different order, so the rows are rebuilt; the selection
    /// still names what it named.
    /// </summary>
    [Fact]
    public void ASnapshotKeepsWhatIsPickedAndTheRowsWhereTheyAre() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Users"),
            ExploreFixture.Folder("Windows"),
        ]);

        var running = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;

        scan.Report(new ExploreProgress(2, null, 0, builder.Build(ExploreChildOrder.ByName)));
        await Task.Yield();

        Assert.False(page.Selection.CanAct);
        Assert.Equal(["Users", "Windows"], page.Rows.Select(row => row.Name));

        page.Selection.Select([users + 1]);
        var windows = page.Rows.Single(row => row.Node == users + 1);
        var changes = Watch(page.Rows);

        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Program Files")]);
        scan.Report(new ExploreProgress(3, null, 0, builder.Build(ExploreChildOrder.ByName)));
        await Task.Yield();

        Assert.Equal([users + 1], page.Selection.Nodes);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Same(windows, page.Rows.Single(row => row.Node == users + 1));

        scan.Finish(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));
        await running;

        Assert.Equal([users + 1], page.Selection.Nodes);
        Assert.Contains(NotifyCollectionChangedAction.Reset, changes);
        Assert.True(page.Selection.CanAct);
    });

    /// <summary>
    /// A cancelled scan takes its last snapshot with it. A partial tree draws and navigates like a
    /// finished one, so leaving it up states a total for the drive that is wrong by whatever was left.
    /// </summary>
    [Fact]
    public void CancellingAScanTakesItsSnapshotOffTheScreen() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Users")]);

        var running = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Report(new ExploreProgress(1, null, 0, builder.Build(ExploreChildOrder.ByName)));
        await Task.Yield();

        Assert.NotNull(page.Tree);

        page.ScanCancelCommand.Execute(null);
        await running;

        Assert.Null(page.Tree);
        Assert.Empty(page.Rows);
        Assert.Empty(page.Trail);
        Assert.Equal("Scan cancelled. Nothing was measured.", page.Status);
        Assert.False(page.IsBusy);
    });

    /// <summary>A step into a folder starts with nothing picked, and a file leads nowhere.</summary>
    [Fact]
    public void AStepIntoAFolderStartsWithNothingPicked() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Users"),
            ExploreFixture.File("pagefile.sys", 100),
        ]);

        builder.AddChildren(users, [ExploreFixture.Folder("testuser")]);
        await _explore.ScanAsync(page, ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));

        page.Descend(users + 1);

        Assert.Equal(ExploreTreeBuilder.RootNode, page.CurrentNode);

        page.Selection.Select([users]);
        page.Descend(users);

        Assert.Equal(users, page.CurrentNode);
        Assert.Empty(page.Selection.Nodes);
        Assert.Equal(["testuser"], page.Rows.Select(row => row.Name));
    });

    /// <summary>
    /// A removal takes its row out of the list, one removal rather than a rebuild. The map still
    /// draws what went, so the folder cannot be opened or picked from it, and the notes button says
    /// one of the notes is a warning.
    /// </summary>
    [Fact]
    public void ARemovedFolderLeavesTheListAndCannotBeOpenedOrPicked() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(Path.GetPathRoot(_explore.Temp.Path)!);
        var page = _explore.Page();
        var (tree, folder) = OnDisk();
        var busy = new List<bool>();

        page.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.NotesDismissed = true;
        var changes = Watch(page.Rows);
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExploreViewModel.IsBusy))
            {
                busy.Add(page.IsBusy);
            }
        };

        page.Selection.Select([folder]);
        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
        Assert.DoesNotContain(page.Rows, row => row.Node == folder);
        Assert.StartsWith("Moved 'old'", page.Status);
        Assert.Equal([true, false], busy);
        Assert.True(page.ShowsNotesButton);
        Assert.Contains("One of them is a warning", page.NotesButtonLabel);

        page.Descend(folder);

        Assert.Equal(tree.RootNode, page.CurrentNode);

        page.Selection.Select([folder]);

        Assert.Empty(page.Selection.Nodes);

        // A rescan is a new tree. What was removed from the last one says nothing about it.
        var (rescan, again) = OnDisk();
        await _explore.ScanAsync(page, ExploreScan.Fast(rescan));

        Assert.Contains(page.Rows, row => row.Node == again);
        Assert.Null(page.Selection.StaleNote);
    });

    /// <summary>
    /// The notes and the button that brings them back take turns, and both are off while there is
    /// nothing to read, which is what makes the button itself the signal.
    /// </summary>
    [Fact]
    public void TheNotesAndTheirButtonTakeTurns() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        Assert.False(page.ShowsNotes);
        Assert.False(page.ShowsNotesButton);

        page.NotesDismissed = true;

        Assert.False(page.ShowsNotesButton);

        await _explore.ScanAsync(page, ExploreScan.Walked(Drive(ExploreFixture.Folder("Users")), FallbackReason.NotElevated));

        Assert.True(page.ShowsNotesButton);
        Assert.False(page.ShowsNotes);
        Assert.DoesNotContain("warning", page.NotesButtonLabel);

        page.NotesDismissed = false;

        Assert.True(page.ShowsNotes);
        Assert.False(page.ShowsNotesButton);
    });

    /// <summary>
    /// The age legend is on screen only where the colours are ages, the picture is a map rather than
    /// the list, and something has been scanned into it.
    /// </summary>
    [Fact]
    public void TheAgeLegendShowsOnlyBesideAMapColouredByAge() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        page.SelectedColouring = ExploreColouring.Age;

        Assert.False(page.ShowsAgeLegend);

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(ExploreFixture.Folder("Users"))));

        Assert.True(page.ShowsAgeLegend);

        page.SelectedView = ExploreView.List;

        Assert.False(page.ShowsAgeLegend);

        page.SelectedView = ExploreView.Icicle;
        page.SelectedColouring = ExploreColouring.Branch;

        Assert.False(page.ShowsAgeLegend);
    });

    /// <summary>
    /// A new scheme rewrites the legend's bands where they are rather than clearing it, and the
    /// bands are the ones the map is drawn in: a legend in one set of colours beside a map in another
    /// names every band wrongly.
    /// </summary>
    [Fact]
    public void ANewSchemeRewritesTheLegendInPlace()
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var changes = Watch(page.AgeLegend);

        page.SelectedScheme = ExploreScheme.Vivid;

        var bands = AgePalette.Bands(ExploreScheme.Vivid);

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(bands.Select(band => band.Label), page.AgeLegend.Select(band => band.Label));
        Assert.Equal(
            bands.Select(band => (band.Colour.Red, band.Colour.Green, band.Colour.Blue)),
            page.AgeLegend.Select(band => (band.Swatch.R, band.Swatch.G, band.Swatch.B)));
        Assert.All(page.AgeLegend, band => Assert.Equal(255, band.Swatch.A));
    }

    /// <summary>
    /// A scan still running is drawn as the icicle whichever view was picked, and the page says so
    /// rather than substituting in silence. The treemap's mouse controls are named only while a
    /// treemap is what is drawn.
    /// </summary>
    [Fact]
    public void ASubstitutedViewIsNamedAndOnlyATreemapOffersItsControls() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Users")]);

        var running = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Report(new ExploreProgress(1, null, 0, builder.Build(ExploreChildOrder.ByName)));
        await Task.Yield();

        page.SelectedView = ExploreView.Treemap;

        Assert.Contains("A treemap reorders every folder", page.ViewNote);
        Assert.False(page.ShowsMapControls);

        page.SelectedView = ExploreView.Sunburst;

        Assert.Contains("A sunburst turns every wedge", page.ViewNote);

        page.SelectedView = ExploreView.List;

        Assert.StartsWith("In name order while the scan runs", page.ViewNote);

        _explore.Scanner.Current.Finish(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));
        await running;

        Assert.Null(page.ViewNote);

        page.SelectedView = ExploreView.Treemap;

        Assert.True(page.ShowsMapControls);

        page.SelectedView = ExploreView.Sunburst;

        Assert.False(page.ShowsMapControls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheUnaccountedNoteIsTheOneForThisProcess(bool isElevated)
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page(isElevated);

        page.Hover(new ExploreHit(ExploreTile.Unaccounted, 1024));

        Assert.Equal("In use, but not accounted for by this scan", page.Hovered);
        Assert.Equal(ExploreUnaccountedNote.For(isElevated), page.HoveredNote);
    }

    /// <summary>
    /// The replacement is pointed where this page is pointed now, which is what the Scan button
    /// beside it would use. A relaunch the user declines says so and keeps this page running; one
    /// that starts tells the window to stand down.
    /// </summary>
    [Fact]
    public void ElevatingSendsWhereThePageIsPointed()
    {
        _explore.Volumes.With(@"C:\").With(@"D:\");
        var page = _explore.Page();
        var replaced = 0;

        page.ReplacedByElevatedInstance += (_, _) => replaced++;
        page.ScopeTo(@"D:\Photos");
        page.ElevateAndRescanCommand.Execute(null);

        Assert.Equal([new ExploreRequest(@"D:\", @"D:\Photos")], _explore.Relaunches);
        Assert.StartsWith("Deguffer is still running without administrator rights", page.Status);
        Assert.Equal(0, replaced);

        _explore.RelaunchStarts = true;
        page.ElevateAndRescanCommand.Execute(null);

        Assert.Equal(1, replaced);
    }

    private static ExploreTree Drive(params ExploreChild[] children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, children);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child named {name}.");
    }

    /// <summary>A folder holding one file, beside a file, on the disk so a removal has something to remove.</summary>
    private (ExploreTree Tree, int Folder) OnDisk()
    {
        var root = _explore.Temp.CreateDirectory("scan");
        _explore.Temp.CreateFile(10, "scan", "old", "a.bin");
        _explore.Temp.CreateFile(5, "scan", "keep.bin");

        var builder = new ExploreTreeBuilder(root);
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("old"),
            ExploreFixture.File("keep.bin", 5),
        ]);

        builder.AddChildren(folder, [ExploreFixture.File("a.bin", 10)]);

        return (builder.Build(ExploreChildOrder.BySize), folder);
    }

    private static List<NotifyCollectionChangedAction> Watch(INotifyCollectionChanged list)
    {
        var changes = new List<NotifyCollectionChangedAction>();

        list.CollectionChanged += (_, e) => changes.Add(e.Action);

        return changes;
    }
}
