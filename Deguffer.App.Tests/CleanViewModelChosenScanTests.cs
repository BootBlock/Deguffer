using System.Collections.Specialized;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Scan button's drop-down: the locations ticked in it, stored as they are ticked, and the scan
/// of those alone, which replaces the list as a full scan does and says how much of the machine it
/// measured.
/// </summary>
public class CleanViewModelChosenScanTests
{
    private static FakeCleanupProvider Provider(string id, ProviderCategory category, long bytes) =>
        new(id) { Category = category, Steps = [Rows.Folder(id, bytes)] };

    /// <summary>
    /// The whole point of the feature: a chosen scan measures the chosen locations and nothing else,
    /// so it is as quick as they are.
    /// </summary>
    [Fact]
    public void AChosenScanMeasuresOnlyTheChosenLocations()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        var npm = Provider("npm", ProviderCategory.PackageCaches, 30);
        using var page = new StoragePage([temp, steam, npm]);

        page.ViewModel.ScanChoice.Choose([temp, npm], chosen: true);
        page.ScanChosen();

        Assert.Equal(1, temp.PlanCount);
        Assert.Equal(0, steam.PlanCount);
        Assert.Equal(1, npm.PlanCount);
        Assert.Equal(["npm", "temp"], page.ViewModel.Findings.Select(row => row.Finding.Provider.Id));
        Assert.StartsWith("Scanned 2 of 3 locations. ", page.ViewModel.Status);
    }

    /// <summary>
    /// A chosen scan replaces the list. A row a full scan built earlier and this scan did not measure
    /// could otherwise be ticked and cleaned on a measurement older than the scan just run.
    /// </summary>
    [Fact]
    public void AChosenScanReplacesTheRowsOfAnEarlierFullScan()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        using var page = new StoragePage([temp, steam]);
        page.Scan();
        Assert.Equal(2, page.ViewModel.Findings.Count);

        page.ViewModel.ScanChoice.Choose([temp], chosen: true);
        page.ScanChosen();

        Assert.Equal("temp", Assert.Single(page.ViewModel.Findings).Finding.Provider.Id);

        // And a full scan afterwards measures everything again and says nothing about coverage.
        page.Scan();
        Assert.Equal(2, page.ViewModel.Findings.Count);
        Assert.DoesNotContain("Scanned", page.ViewModel.Status);
    }

    /// <summary>
    /// Nothing chosen is nothing to scan: the drop-down's Scan stays off until something is ticked,
    /// and goes off again while a scan runs.
    /// </summary>
    [Fact]
    public void TheChosenScanIsOfferedOnlyWithSomethingChosen()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        using var page = new StoragePage([temp]);
        var command = page.ViewModel.PreviewChosenCommand;
        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        Assert.False(command.CanExecute(null));

        page.ViewModel.ScanChoice.Choose([temp], chosen: true);

        Assert.True(command.CanExecute(null));
        Assert.True(raised > 0);

        bool? offeredWhileScanning = null;
        temp.WhilePlanning = () => offeredWhileScanning = command.CanExecute(null);
        page.ScanChosen();
        Assert.False(offeredWhileScanning);
        Assert.True(command.CanExecute(null));

        page.ViewModel.ScanChoice.Choose([temp], chosen: false);

        Assert.False(command.CanExecute(null));
    }

    /// <summary>
    /// A tick that cannot be stored is not applied, as a setting is not: the box goes back, and a
    /// chosen scan measures what the list shows ticked rather than a choice that exists nowhere else.
    /// </summary>
    [Fact]
    public void ATickThatCannotBeStoredIsNotAppliedAndTheBoxGoesBack()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        using var page = new StoragePage([temp]);
        var choice = page.ViewModel.ScanChoice;
        var row = choice.Shown.SelectMany(group => group).Single();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // A folder where the file goes, so the write is refused as a locked profile refuses it.
        Directory.CreateDirectory(Path.Combine(page.Environment.LocalAppData, "Deguffer", "preferences.json"));

        row.IsChosen = true;

        Assert.True(choice.SaveFailed);
        Assert.False(row.IsChosen);
        Assert.Contains(nameof(row.IsChosen), raised);
        Assert.False(page.ViewModel.PreviewChosenCommand.CanExecute(null));
    }

    /// <summary>
    /// The choice is stored as it is ticked, so the user who always clears the temporary folders
    /// ticks them once, and a page built later starts from it.
    /// </summary>
    [Fact]
    public void ATickIsStoredAndAPageBuiltLaterStartsFromIt()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        using var page = new StoragePage([temp, steam]);
        var row = page.ViewModel.ScanChoice.Shown.SelectMany(group => group).Single(row => row.Provider == steam);

        row.IsChosen = true;

        Assert.Equal(["steam"], page.Preferences.Current.ChosenToScan.Ids);

        var later = new ViewModels.ScanChoiceViewModel([temp, steam], page.Preferences);
        Assert.Equal([steam], later.Chosen);
        Assert.True(later.Shown.SelectMany(group => group).Single(row => row.Provider == steam).IsChosen);
    }

    /// <summary>
    /// A heading's tick takes every row the search shows under it, and only those. A search for one
    /// word and a tick on its heading must not choose rows the reader cannot see.
    /// </summary>
    [Fact]
    public void AHeadingsTickCoversTheRowsTheSearchShows()
    {
        var steam = new FakeCleanupProvider("steam") { Name = "Steam HTTP cache", Category = ProviderCategory.Games };
        var epic = new FakeCleanupProvider("epic") { Name = "Epic Games launcher logs", Category = ProviderCategory.Games };
        using var page = new StoragePage([steam, epic]);
        var choice = page.ViewModel.ScanChoice;

        choice.Search = "steam";
        var games = Assert.Single(choice.Shown);
        Assert.False(games.State);

        games.ToggleCommand.Execute(null);

        Assert.Equal([steam], choice.Chosen);
        Assert.True(games.State);

        choice.Search = string.Empty;

        // Both rows are back under the heading, one ticked, so the heading is mixed.
        Assert.Equal(2, games.Count);
        Assert.Null(games.State);

        // A click on a mixed heading clears it, as a click on any heading that is not clear does.
        games.ToggleCommand.Execute(null);

        Assert.Empty(choice.Chosen);
        Assert.False(games.State);
    }

    /// <summary>
    /// Typing in the search changes which rows are shown, and the list is told one change per row
    /// that went, never a reset that would throw away the reader's place.
    /// </summary>
    [Fact]
    public void ASearchUpdatesTheListInPlace()
    {
        var steam = new FakeCleanupProvider("steam") { Name = "Steam HTTP cache", Category = ProviderCategory.Games };
        var epic = new FakeCleanupProvider("epic") { Name = "Epic Games launcher logs", Category = ProviderCategory.Games };
        var temp = new FakeCleanupProvider("temp") { Name = "Temporary files", Category = ProviderCategory.TemporaryFiles };
        using var page = new StoragePage([steam, epic, temp]);
        var choice = page.ViewModel.ScanChoice;
        var games = choice.Shown.Single(group => group.Category == ProviderCategory.Games);
        var actions = new List<NotifyCollectionChangedAction>();
        choice.Shown.CollectionChanged += (_, e) => actions.Add(e.Action);
        games.CollectionChanged += (_, e) => actions.Add(e.Action);

        choice.Search = "steam";

        Assert.Same(games, Assert.Single(choice.Shown));
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove], actions);
    }

    /// <summary>
    /// The drop-down says which locations the last scan that measured them found not installed, and
    /// a later chosen scan of something else does not forget it.
    /// </summary>
    [Fact]
    public void ALocationFoundNotInstalledIsMarkedUntilAScanFindsIt()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        steam.IsPresent = false;
        using var page = new StoragePage([temp, steam]);
        var rows = page.ViewModel.ScanChoice.Shown.SelectMany(group => group).ToDictionary(row => row.Provider.Id);

        Assert.False(rows["steam"].WasMissing);

        page.Scan();
        Assert.True(rows["steam"].WasMissing);
        Assert.False(rows["temp"].WasMissing);
        Assert.Equal("steam, not found last scan", rows["steam"].AccessibleName);

        page.ViewModel.ScanChoice.Choose([temp], chosen: true);
        page.ScanChosen();
        Assert.True(rows["steam"].WasMissing);

        steam.IsPresent = true;
        page.Scan();
        Assert.False(rows["steam"].WasMissing);
    }

    /// <summary>
    /// Elevating measures again what the last scan measured. A user who scanned a chosen location and
    /// asked for administrator rights asked for that one: not for every location, and not for one
    /// ticked after the scan ran.
    /// </summary>
    [Fact]
    public void ElevatingAfterAChosenScanAsksForThatScanAgain()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        using var page = new StoragePage([temp, steam]);

        page.ViewModel.ElevateAndRescanCommand.Execute(null);

        page.ViewModel.ScanChoice.Choose([temp], chosen: true);
        page.ScanChosen();
        page.ViewModel.ScanChoice.Choose([steam], chosen: true);
        page.ViewModel.ElevateAndRescanCommand.Execute(null);

        page.Scan();
        page.ViewModel.ElevateAndRescanCommand.Execute(null);

        Assert.Equal(
            ["every", "temp", "every"],
            page.Relaunches.Select(request => Assert.IsType<PreviewRequest>(request).Chosen is { } chosen
                ? string.Join(",", chosen.Ids)
                : "every"));
    }

    /// <summary>
    /// The elevated window scans the locations the replaced one scanned, whatever is ticked in its own
    /// list, so the scan it repeats is the one the user was looking at.
    /// </summary>
    [Fact]
    public void AChosenScanHandedOverScansThoseLocationsWhateverIsTicked()
    {
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var steam = Provider("steam", ProviderCategory.Games, 20);
        using var page = new StoragePage([temp, steam]);
        page.ViewModel.ScanChoice.Choose([steam], chosen: true);

        UiThread.Run(() => page.ViewModel.PreviewChosenCommand.ExecuteAsync(new ProviderChoice { Ids = ["temp"] }));

        Assert.Equal(1, temp.PlanCount);
        Assert.Equal(0, steam.PlanCount);
        Assert.Equal("temp", Assert.Single(page.ViewModel.Findings).Finding.Provider.Id);
    }

    /// <summary>
    /// Cancel stops a chosen scan as it stops a full one: the locations already being checked finish,
    /// and none of the rest is started. Every other location waits until Cancel has been pressed, so
    /// only the few a scan checks at once can have started, far fewer than are chosen.
    /// </summary>
    [Fact]
    public void CancelStopsAChosenScan()
    {
        using var cancelled = new ManualResetEventSlim();
        var temp = Provider("temp", ProviderCategory.TemporaryFiles, 10);
        var games = Enumerable.Range(0, 40).Select(index => Provider($"game{index}", ProviderCategory.Games, 20)).ToList();
        using var page = new StoragePage([temp, .. games]);
        page.ViewModel.ScanChoice.Choose([temp, .. games], chosen: true);

        temp.WhilePlanning = () =>
        {
            page.ViewModel.CancelCommand.Execute(null);
            cancelled.Set();
        };

        foreach (var game in games)
        {
            game.WhilePlanning = () => cancelled.Wait(TimeSpan.FromSeconds(10));
        }

        page.ScanChosen();

        Assert.Equal("Scan cancelled. Nothing was changed.", page.ViewModel.Status);
        Assert.InRange(games.Sum(game => game.PlanCount), 0, games.Count / 2);
    }
}
