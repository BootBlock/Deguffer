using System.Collections.Specialized;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
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
        Assert.Empty(page.Legend);
        Assert.Empty(_explore.Relaunches);
    }

    [Fact]
    public void AnElevatedPageOffersNoElevation()
    {
        _explore.Volumes.With(@"C:\");

        Assert.False(_explore.Page(isElevated: true).CanElevate);
    }

    /// <summary>
    /// Elevating ends this process, and a clean still running on the Storage page would end with
    /// it, unverified and unreported. The button waits for it, and comes back when it ends. Scanning
    /// does not end the process, so it is not held.
    /// </summary>
    [Fact]
    public void ElevatingWaitsForAnActionRunningOnAnotherPage()
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var raised = 0;
        page.ElevateAndRescanCommand.CanExecuteChanged += (_, _) => raised++;

        var clean = _explore.Running.Begin(RunningAction.StorageClean);

        Assert.False(page.ElevateAndRescanCommand.CanExecute(null));
        Assert.True(page.ScanCommand.CanExecute(null));

        clean.Dispose();

        Assert.True(page.ElevateAndRescanCommand.CanExecute(null));
        Assert.Equal(2, raised);
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
        var raised = new List<string?>();

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        page.Selection.Select([Child(tree, "kept")]);

        Assert.Equal("Kept for a reason.", page.Selection.Note);
        Assert.True(page.ShowsNotes);

        // The notes are the selection's as much as the page's, and the card follows the page.
        Assert.Contains(nameof(ExploreViewModel.ShowsNotes), raised);
        Assert.Contains(nameof(ExploreViewModel.ShowsNotesButton), raised);
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
    /// A choice that is not a folder on a disk is not thrown about from an <c>async void</c> handler,
    /// which ends the process. It leaves the target where it was and says why, and the next folder
    /// chosen takes the sentence back.
    /// </summary>
    [Fact]
    public void AChoiceThatIsNotAFolderOnADiskLeavesTheTargetAndSaysSo()
    {
        _explore.Volumes.With(@"C:\").With(@"D:\");
        var page = _explore.Page();
        page.ScopeTo(@"D:\Photos");

        page.ScopeTo(string.Empty);

        Assert.Equal(@"D:\", page.SelectedDrive?.RootPath);
        Assert.Equal(@"D:\Photos", page.ScopeFolder);
        Assert.Equal(PickedFolder.NotOnDisk, page.Status);

        page.ScopeTo(@"C:\Users\testuser");

        Assert.Equal(@"C:\Users\testuser", page.ScopeFolder);
        Assert.Equal(ScanPrompt, page.Status);
    }

    /// <summary>
    /// A choice that is not a folder on a disk leaves a refused target where it was, so the refusal
    /// is still said beside it: Scan stays greyed out, and the reason must not go with the choice.
    /// </summary>
    [Fact]
    public void AChoiceThatIsNotAFolderOnADiskKeepsTheStandingRefusalSaid()
    {
        _explore.Volumes.With(@"C:\").With(@"C:\Cloud\", features: VolumeFeatures.RemoteStorage);
        var page = _explore.Page();
        page.ScopeTo(@"C:\Cloud\Photos");

        page.ScopeTo(string.Empty);

        Assert.Equal($"{PickedFolder.NotOnDisk} {DriveChoice.RemoteStorageRefusal}", page.Status);
        Assert.False(page.ScanCommand.CanExecute(null));

        page.ScopeTo(@"C:\Users\testuser");

        Assert.Equal(ScanPrompt, page.Status);
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

    /// <summary>
    /// A page restored to a folder before any drive was listed has the box filled in by the first
    /// reading. That is not a move: the scan was never pointed at a drive, so the folder and the
    /// offer its scan made both stay.
    /// </summary>
    [Fact]
    public void AReadingThatGivesTheBoxItsFirstDriveKeepsTheFolderAndTheOffer() => UiThread.Run(async () =>
    {
        var page = _explore.Page();

        Assert.True(page.PointAt(null, @"C:\Users\testuser"));
        Assert.Null(page.SelectedDrive);

        await _explore.ScanAsync(page, ExploreScan.Fast(new ExploreTreeBuilder(@"C:\Users\testuser").Build(ExploreChildOrder.BySize)));

        Assert.False(page.CanElevate);

        _explore.Volumes.With(@"C:\");
        _explore.Time.Advance(DriveList.FreshFor);
        page.RefreshDrives();

        Assert.Equal(@"C:\", page.SelectedDrive?.RootPath);
        Assert.Equal(@"C:\Users\testuser", page.ScopeFolder);
        Assert.False(page.CanElevate);
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
        Assert.Equal(
            $"{FreeSpace.Format(0)} on disk accounted for, in files whose lengths add up to {FreeSpace.Format(0)}.",
            page.Status);

        builder.MarkSizeUnknown(refused);
        await _explore.ScanAsync(page, ExploreScan.Walked(builder.Build(ExploreChildOrder.BySize), FallbackReason.NotElevated));

        Assert.Contains("Some of this drive could not be read, so the totals are lower bounds.", page.Status);
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
        var (tree, folder) = _explore.OnDisk();
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
        var (rescan, again) = _explore.OnDisk();
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
    /// The legend is on screen only where the colours are a scale, ages or growth, the picture is a
    /// map rather than the list, and something has been scanned into it.
    /// </summary>
    [Fact]
    public void TheLegendShowsOnlyBesideAMapColouredByAScale() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        page.SelectedColouring = ExploreColouring.Age;

        Assert.False(page.ShowsLegend);

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(ExploreFixture.Folder("Users"))));

        Assert.True(page.ShowsLegend);

        page.SelectedView = ExploreView.List;

        Assert.False(page.ShowsLegend);

        page.SelectedView = ExploreView.Icicle;
        page.SelectedColouring = ExploreColouring.Growth;

        Assert.True(page.ShowsLegend);

        page.SelectedColouring = ExploreColouring.Branch;

        Assert.False(page.ShowsLegend);
        Assert.Empty(page.Legend);
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
        page.SelectedColouring = ExploreColouring.Age;
        var changes = Watch(page.Legend);

        page.SelectedScheme = ExploreScheme.Vivid;

        var bands = AgePalette.Bands(ExploreScheme.Vivid);

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(bands.Select(band => band.Label), page.Legend.Select(band => band.Label));
        Assert.Equal(
            bands.Select(band => (band.Colour.Red, band.Colour.Green, band.Colour.Blue)),
            page.Legend.Select(band => (band.Swatch.R, band.Swatch.G, band.Swatch.B)));
        Assert.All(page.Legend, band => Assert.Equal(255, band.Swatch.A));
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
    public void TheUnaccountedNoteIsTheOneForThisProcessAndThisVolume(bool isElevated) => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        _explore.Hidden.Answer = new HiddenSpace(ShadowStorage.Refused, new ReservedStorage(Statement.Stated, 500));
        var page = _explore.Page(isElevated);
        var tree = Drive(ExploreFixture.File("file", 3_000));

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));
        page.Hover(new ExploreHit(ExploreTile.Unaccounted, 2_500));

        Assert.Equal("In use, but not accounted for by this scan", page.Hovered);
        Assert.Equal(ExploreUnaccountedNote.For(isElevated, page.Volume, tree.TotalBytes, ScanStrategy.MasterFileTable), page.HoveredNote);
        Assert.Contains("Reserved storage", page.HoveredNote);
    });

    /// <summary>
    /// Windows' figures are read once, for the volume the finished scan covered, and each block they
    /// draw is described from them.
    /// </summary>
    [Fact]
    public void AWholeVolumeIsDrawnWithWindowsFiguresReadOnceForIt() => UiThread.Run(async () =>
    {
        var shadow = new ShadowStorage(Statement.Stated, 400, 1_000, 2_000);
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        _explore.Hidden.Answer = new HiddenSpace(shadow, new ReservedStorage(Statement.Stated, 500));
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(ExploreFixture.File("file", 3_000))));

        Assert.Equal([@"C:\"], _explore.Hidden.Asked);
        Assert.Equal(new VolumeParts(1_000, 500, 1_500, 6_000), page.VolumeBeside.Parts(3_000));

        page.Hover(new ExploreHit(ExploreTile.ShadowCopies, 1_000));

        Assert.Equal("Restore points and shadow copies, by Windows' own figure", page.Hovered);
        Assert.Equal(FreeSpace.Format(1_000), page.HoveredFigures);
        Assert.Equal(HiddenSpaceNote.ShadowCopies(shadow), page.HoveredNote);

        page.Hover(new ExploreHit(ExploreTile.ReservedStorage, 500));

        Assert.Equal("Reserved storage, by Windows' own figure", page.Hovered);
        Assert.Equal(HiddenSpaceNote.ReservedStorage(), page.HoveredNote);
    });

    /// <summary>
    /// A scan that walked for a reason elevating cannot fix still offers it where Windows refused a
    /// figure, because the elevated scan would draw that figure.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARefusedFigureKeepsTheElevationOffered(bool refused) => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        _explore.Hidden.Answer = refused ? new HiddenSpace(ShadowStorage.Refused, default) : HiddenSpace.None;
        var page = _explore.Page();

        await _explore.ScanAsync(
            page, ExploreScan.Walked(Drive(ExploreFixture.File("file", 3_000)), FallbackReason.NotNtfsVolume));

        Assert.Equal(refused, page.CanElevate);
    });

    /// <summary>
    /// Where the scan counted System Volume Information, the storage is inside it, and pointing at it
    /// says how much of it Windows states is the storage.
    /// </summary>
    [Fact]
    public void SystemVolumeInformationCountedByTheScanSaysWhatWindowsStatesIsInIt() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        _explore.Hidden.Answer = new HiddenSpace(new ShadowStorage(Statement.Stated, 400, 1_000, 2_000), default);
        var page = _explore.Page();
        var tree = Drive(ExploreFixture.Folder(VolumeSpace.SystemVolumeInformation), ExploreFixture.File("file", 3_000));

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));
        page.Hover(new ExploreHit(Child(tree, VolumeSpace.SystemVolumeInformation), 0));

        Assert.True(page.Volume.CountedSystemVolumeInformation);
        Assert.EndsWith(
            HiddenSpaceNote.For(tree, Child(tree, VolumeSpace.SystemVolumeInformation), page.Volume),
            page.HoveredNote);
        Assert.Contains(FreeSpace.Format(1_000), page.HoveredNote);
    });

    /// <summary>
    /// #257: pointing at a shape says its space on disk, and its length and why where those differ,
    /// so a large file drawn as nothing is explained where the reader is looking.
    /// </summary>
    [Fact]
    public void PointingAtAShapeSaysItsSpaceOnDiskAndWhyItIsNotItsLength() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        var page = _explore.Page();
        var tree = Drive(
            new ExploreChild("film.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 5_000_000, Storage: FileStorage.CloudOnly),
            ExploreFixture.File("file", 3_000));

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));
        page.Hover(new ExploreHit(Child(tree, "film.mkv"), 0));

        Assert.StartsWith(
            $"{FreeSpace.Format(0)} on disk, {FreeSpace.Format(5_000_000)} long, online-only, last written ",
            page.HoveredFigures);

        page.Hover(new ExploreHit(Child(tree, "file"), 3_000));

        Assert.StartsWith($"{FreeSpace.Format(3_000)} on disk, last written ", page.HoveredFigures);
    });

    /// <summary>
    /// The status line says which figure the map draws, with the length beside it. A walk says what
    /// its figures leave out, and a scan that counted more than the drive has in use says so rather
    /// than showing a drive that only appears to add up.
    /// </summary>
    [Fact]
    public void TheStatusLineSaysWhatTheFiguresAreAndWhereTheyCountTooMuch() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 12_000, freeBytes: 6_000);
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(
            new ExploreChild("film.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 9_000, Storage: FileStorage.CloudOnly),
            ExploreFixture.File("file", 3_000))));

        Assert.Equal(
            $"{FreeSpace.Format(3_000)} on disk accounted for, in files whose lengths add up to {FreeSpace.Format(12_000)}.",
            page.Status);

        await _explore.ScanAsync(page, ExploreScan.Walked(Drive(ExploreFixture.File("linked", 7_000)), FallbackReason.NotElevated));

        Assert.Contains(ExploreRouteText.Sizing(ScanStrategy.ParallelEnumeration)!, page.Status);
        Assert.EndsWith(
            ExploreUnaccountedNote.Overcount(page.Volume, 7_000, ScanStrategy.ParallelEnumeration)!,
            page.Status);
    });

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

    private static List<NotifyCollectionChangedAction> Watch(INotifyCollectionChanged list)
    {
        var changes = new List<NotifyCollectionChangedAction>();

        list.CollectionChanged += (_, e) => changes.Add(e.Action);

        return changes;
    }
}
