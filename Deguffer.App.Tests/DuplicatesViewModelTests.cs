using System.Collections.Specialized;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Duplicates page wires Core's search (§7.4): each control hands the search the value it
/// shows, the choices are remembered, groups go in where the marks place them without moving any
/// row already shown, and the elevated reopen carries every location in its role. What a search
/// finds and where a group belongs is proved in Core.
/// </summary>
public sealed class DuplicatesViewModelTests : DuplicatesPageScene
{
    public static TheoryData<string> Controls =>
    [
        nameof(DuplicateFiltersViewModel.MatchName),
        nameof(DuplicateFiltersViewModel.MatchSize),
        nameof(DuplicateFiltersViewModel.MatchModified),
        nameof(DuplicateFiltersViewModel.MatchContent),
        nameof(DuplicateFiltersViewModel.AlgorithmIndex),
        nameof(DuplicateFiltersViewModel.SmallestAmount),
        nameof(DuplicateFiltersViewModel.SmallestUnitIndex),
        nameof(DuplicateFiltersViewModel.LargestAmount),
        nameof(DuplicateFiltersViewModel.LargestUnitIndex),
        nameof(DuplicateFiltersViewModel.ExtensionModeIndex),
        nameof(DuplicateFiltersViewModel.Extensions),
        nameof(DuplicateFiltersViewModel.SearchHidden),
        nameof(DuplicateFiltersViewModel.SearchSystem),
        nameof(DuplicateFiltersViewModel.SearchPassedOverPlaces),
    ];

    /// <summary>
    /// Each criterion and filter control, set away from its default, shows what it was set to, and
    /// the search is handed exactly that, and the same control on a page opened afterwards shows it
    /// still.
    /// </summary>
    [Theory]
    [MemberData(nameof(Controls))]
    public void EachControlHandsTheSearchTheValueItShows(string control) => UiThread.Run(async () =>
    {
        var page = PageWithPhotos();
        var filters = page.Filters;
        var sha256 = ChecksumAlgorithms.Offered.ToList().IndexOf(ChecksumAlgorithm.Sha256);

        // The checks on what a control shows and what the search got, for the one control set.
        (Action Set, Func<object> Shown, Action<DuplicateSearch> Got) = control switch
        {
            nameof(filters.MatchName) => Case(() => filters.MatchName = true, () => filters.MatchName,
                search => Assert.True(search.Criteria.HasFlag(MatchCriteria.Name))),
            nameof(filters.MatchSize) => Case(() => filters.MatchSize = true, () => filters.MatchSize,
                search => Assert.True(search.Criteria.HasFlag(MatchCriteria.Size))),
            nameof(filters.MatchModified) => Case(() => filters.MatchModified = true, () => filters.MatchModified,
                search => Assert.True(search.Criteria.HasFlag(MatchCriteria.Modified))),
            nameof(filters.MatchContent) => Case(() => (filters.MatchSize, filters.MatchContent) = (true, false), () => filters.MatchContent,
                search => Assert.Equal(MatchCriteria.Size, search.Criteria)),
            nameof(filters.AlgorithmIndex) => Case(() => filters.AlgorithmIndex = sha256, () => filters.AlgorithmIndex,
                search => Assert.Equal(ChecksumAlgorithm.Sha256, search.Algorithm)),
            nameof(filters.SmallestAmount) => Case(() => filters.SmallestAmount = 2, () => filters.SmallestAmount,
                search => Assert.Equal(2L * 1024 * 1024, search.Sizes.Smallest)),
            nameof(filters.SmallestUnitIndex) => Case(() => (filters.SmallestUnitIndex, filters.SmallestAmount) = ((int)SizeUnit.KB, 3), () => filters.SmallestUnitIndex,
                search => Assert.Equal(3L * 1024, search.Sizes.Smallest)),
            nameof(filters.LargestAmount) => Case(() => filters.LargestAmount = 5, () => filters.LargestAmount,
                search => Assert.Equal(5L * 1024 * 1024, search.Sizes.Largest)),
            nameof(filters.LargestUnitIndex) => Case(() => (filters.LargestUnitIndex, filters.LargestAmount) = ((int)SizeUnit.GB, 1), () => filters.LargestUnitIndex,
                search => Assert.Equal(1L * 1024 * 1024 * 1024, search.Sizes.Largest)),
            nameof(filters.ExtensionModeIndex) => Case(() => filters.ExtensionModeIndex = (int)ExtensionFilterMode.SkipThese, () => filters.ExtensionModeIndex,
                search => Assert.Equal(ExtensionFilterMode.SkipThese, search.Extensions.Mode)),
            nameof(filters.Extensions) => Case(() => (filters.ExtensionModeIndex, filters.Extensions) = ((int)ExtensionFilterMode.OnlyThese, "jpg png"), () => filters.Extensions,
                search => Assert.Equal([".jpg", ".png"], search.Extensions.Extensions)),
            nameof(filters.SearchHidden) => Case(() => filters.SearchHidden = true, () => filters.SearchHidden,
                search => Assert.True(search.SearchHidden)),
            nameof(filters.SearchSystem) => Case(() => filters.SearchSystem = true, () => filters.SearchSystem,
                search => Assert.True(search.SearchSystem)),
            nameof(filters.SearchPassedOverPlaces) => Case(() => filters.SearchPassedOverPlaces = true, () => filters.SearchPassedOverPlaces,
                search => Assert.True(search.SearchPassedOverPlaces)),
            _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
        };

        var before = Shown();
        Set();
        var shown = Shown();

        Assert.NotEqual(before, shown);
        Got(await SearchedWith(page));

        // Remembered: a page opened afterwards, on the same preferences, shows the same.
        var reopened = typeof(DuplicateFiltersViewModel).GetProperty(control)!.GetValue(Page().Filters);
        Assert.Equal(shown, reopened);
    });

    private static (Action Set, Func<object> Shown, Action<DuplicateSearch> Got) Case(
        Action set, Func<object> shown, Action<DuplicateSearch> got) => (set, shown, got);

    /// <summary>
    /// The locations are handed over in their order and their roles: a drive added from the picker, a
    /// folder from the folder dialog, and a folder switched to a reference.
    /// </summary>
    [Fact]
    public void TheLocationsAreHandedOverInTheirRoles() => UiThread.Run(async () =>
    {
        var page = Page();
        page.Locations.RefreshDrives();
        page.Locations.SelectedDrive = page.Locations.Drives.Single(drive => drive.RootPath == @"D:\");
        page.Locations.AddDriveCommand.Execute(null);
        page.Locations.AddFolder(Photos);
        page.Locations.Rows[1].IsReference = true;

        var search = await SearchedWith(page);

        Assert.Equal([new SearchLocation(@"D:\"), new SearchLocation(Photos, LocationRole.Reference)], search.Locations);
    });

    [Fact]
    public void ALocationThatCannotBeAddedSaysWhyAndIsNotAdded()
    {
        var page = PageWithPhotos();

        page.Locations.AddFolder(Photos);
        Assert.Equal(LocationChoice.AlreadyListed, page.Locations.Note);

        page.Locations.AddFolder("Photos");
        Assert.Equal(LocationChoice.NotOnDisk, page.Locations.Note);

        Assert.Single(page.Locations.Rows);
    }

    /// <summary>A search that cannot run says why and cannot be started, by Core's words.</summary>
    [Fact]
    public void ASearchThatCannotRunSaysWhy()
    {
        var page = Page();

        Assert.Equal("Choose at least one drive or folder to search.", page.WhyCannotSearch);
        Assert.False(page.SearchCommand.CanExecute(null));

        page.Locations.AddFolder(Photos);
        Assert.True(page.SearchCommand.CanExecute(null));

        page.Filters.MatchContent = false;
        Assert.Equal("Choose at least one thing two files must share to be a match.", page.WhyCannotSearch);
        Assert.False(page.SearchCommand.CanExecute(null));
    }

    /// <summary>
    /// The reader's place holds while groups stream in: each group arriving is one insertion, at the
    /// place the marks put it, and no row already shown is moved, replaced or rebuilt.
    /// </summary>
    [Fact]
    public void TheListKeepsTheReadersPlaceWhileGroupsStreamIn() => UiThread.Run(async () =>
    {
        DuplicateGroup[] arriving =
        [
            _scene.Pair("a.jpg", 8192),
            _scene.Pair("b.jpg", 4096),
            _scene.Pair("c.jpg", 16384),
            _scene.Pair("d.jpg", 8192),
        ];
        var page = PageWithPhotos(Finds(arriving));
        List<NotifyCollectionChangedEventArgs> changes = [];
        List<DuplicateGroupRow> firstShown = [];

        page.Groups.CollectionChanged += (_, change) =>
        {
            changes.Add(change);

            // The first row shown, kept to prove it is still the same row at the end.
            if (firstShown.Count == 0 && page.Groups.Count > 0)
            {
                firstShown.Add(page.Groups[0]);
            }
        };

        await page.SearchCommand.ExecuteAsync(null);

        // The one reset is the new search clearing the last one's list, before any group arrived.
        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0].Action);
        Assert.All(changes.Skip(1), change => Assert.Equal(NotifyCollectionChangedAction.Add, change.Action));
        Assert.Equal([0, 1, 0, 2], changes.Skip(1).Select(change => change.NewStartingIndex));
        Assert.Equal(["c.jpg", "a.jpg", "d.jpg", "b.jpg"], page.Groups.Select(row => row.Copies[0].Copy.Name));
        Assert.Same(firstShown[0], page.Groups[1]);
        Assert.Equal(page.Marks!.Groups, page.Groups.Select(row => row.Marks));
    });

    [Fact]
    public void ANameOrSizeGroupSaysItsFilesMayDifferAndAContentGroupDoesNot() => UiThread.Run(async () =>
    {
        var page = PageWithPhotos(Finds(
            _scene.Pair("content.jpg", 8192),
            _scene.Pair("name.jpg", 4096, MatchCriteria.Name),
            _scene.Pair("size.jpg", 2048, MatchCriteria.Size)));

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(
            [("content.jpg", false), ("name.jpg", true), ("size.jpg", true)],
            page.Groups.Select(row => (row.Copies[0].Copy.Name, row.HasMayDiffer)));
        Assert.All(page.Groups.Where(row => row.HasMayDiffer), row => Assert.Equal(row.Marks.Group.MayDiffer, row.MayDiffer));
    });

    [Fact]
    public void ElevatingCarriesEveryLocationInItsRole()
    {
        var page = PageWithPhotos();
        page.Locations.AddFolder(_scene.Folder("Backup"));
        page.Locations.Rows[1].IsReference = true;

        page.ElevateCommand.Execute(null);

        var request = Assert.IsType<DuplicatesRequest>(Assert.Single(_relaunches));
        Assert.Equal([new SearchLocation(Photos), new SearchLocation(_scene.Folder("Backup"), LocationRole.Reference)], request.Locations);
    }

    /// <summary>The elevated page opens with the locations it was handed, in their roles, ready to search them.</summary>
    [Fact]
    public void AnElevatedPageOpensWithTheLocationsItWasHanded()
    {
        var page = Page(requested: new DuplicatesRequest([new(Photos, LocationRole.Reference), new(@"D:\")]));

        Assert.True(page.IsRequested);
        Assert.Equal([new SearchLocation(Photos, LocationRole.Reference), new SearchLocation(@"D:\")], page.Locations.Chosen);
        Assert.True(page.SearchCommand.CanExecute(null));
    }

    /// <summary>
    /// Stopped, the search keeps the groups it confirmed and says it stopped, in the headline and in
    /// the notes, so the files it never read are not taken as files with no match.
    /// </summary>
    [Fact]
    public void StoppingKeepsTheConfirmedGroupsAndSaysSo() => UiThread.Run(async () =>
    {
        var kept = _scene.Pair("kept.jpg", 4096);

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(kept);
            marksMade(_scene.Marks(candidates));
            finding.Report(candidates);
            found.Report(kept);

            // As Core's search does: a stop while reading answers what was confirmed, marked stopped.
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);

            return new DuplicateSearchResult(candidates, [kept], default, Stopped: true);
        };

        var page = PageWithPhotos(run);
        var searching = page.SearchCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => page.Groups.Count == 1, "the first group arriving");

        page.StopCommand.Execute(null);
        await searching;

        Assert.Equal(["kept.jpg"], page.Groups.Select(row => row.Copies[0].Copy.Name));
        Assert.StartsWith("Stopped.", page.Headline, StringComparison.Ordinal);
        Assert.Equal(DuplicateSearchNotes.Stopped, page.Notes[^1]);
        Assert.False(page.IsSearching);
    });

    /// <summary>
    /// The Remove button takes its location out of the search, and removing the only one leaves
    /// nothing to search, so the search cannot start and says why.
    /// </summary>
    [Fact]
    public void RemovingALocationTakesItOutOfTheSearch() => UiThread.Run(async () =>
    {
        var page = PageWithPhotos();
        page.Locations.AddFolder(_scene.Folder("Backup"));

        page.Locations.Rows[0].RemoveCommand.Execute(null);

        Assert.Equal([new SearchLocation(_scene.Folder("Backup"))], (await SearchedWith(page)).Locations);

        page.Locations.Rows[0].RemoveCommand.Execute(null);

        Assert.Empty(page.Locations.Chosen);
        Assert.Equal("Choose at least one drive or folder to search.", page.WhyCannotSearch);
        Assert.False(page.SearchCommand.CanExecute(null));
    });

    /// <summary>
    /// Each copy's row shows its own path, cut where Core says it differs from the others', so the
    /// part picked out is that copy's and not another's.
    /// </summary>
    [Fact]
    public void EachCopyShowsWhereItsOwnPathDiffers() => UiThread.Run(async () =>
    {
        var group = DuplicateScene.Group(
            MatchCriteria.Content,
            _scene.Copy(Path.Combine(_scene.Folder("Documents"), "Trip", "beach.jpg")),
            _scene.Copy(Path.Combine(_scene.Folder("Downloads"), "beach.jpg")),
            _scene.Copy(Path.Combine(_scene.Folder("Documents"), "Trip 2", "beach.jpg")));
        var page = PageWithPhotos(Finds(group));

        await page.SearchCommand.ExecuteAsync(null);

        var copies = Assert.Single(page.Groups).Copies;
        var paths = PathDifference.Of([.. group.Files.Select(copy => copy.Path)]);

        // The fixture holds three different differences, so a row given another copy's parts shows.
        Assert.Equal(3, paths.Select(parts => parts.Differs).Distinct().Count());
        Assert.Equal(group.Files, copies.Select(row => row.Copy));
        Assert.Equal(paths, copies.Select(row => new PathParts(row.Same, row.Differs, row.SameEnd)));
    });

    /// <summary>
    /// A group, or the end of a search, that arrives before the marks were handed over is a broken
    /// contract, and the page says so rather than leaving the group out, which would read as files
    /// with no duplicate.
    /// </summary>
    [Fact]
    public void AGroupArrivingWithoutTheMarksIsAnError() => Assert.Throws<InvalidOperationException>(() => UiThread.Run(async () =>
    {
        var group = _scene.Pair("a.jpg", 4096);

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(group);
            found.Report(group);

            // After the group's post, and handed over then, so the end of the search finds marks and
            // only the group itself can say they were missing.
            await Task.Yield();
            marksMade(_scene.Marks(candidates));

            return new DuplicateSearchResult(candidates, [group], default, Stopped: false);
        };

        await PageWithPhotos(run).SearchCommand.ExecuteAsync(null);
    }));

    [Fact]
    public void ASearchEndingWithoutTheMarksIsAnError() => UiThread.Run(async () =>
    {
        RunDuplicateSearch run = (search, marksMade, finding, found, progress, ct) =>
            Task.FromResult(new DuplicateSearchResult(DuplicateScene.Finding(), [], default, Stopped: false));
        var page = PageWithPhotos(run);

        await Assert.ThrowsAsync<InvalidOperationException>(() => page.SearchCommand.ExecuteAsync(null));
        Assert.Empty(page.Groups);
    });

    /// <summary>
    /// The results are saved where the dialog says, one row a copy, with the groups numbered in the
    /// order the page shows them, which is not the order they arrived in.
    /// </summary>
    [Fact]
    public void TheResultsAreSavedWhereTheDialogSaysInTheOrderShown() => UiThread.Run(async () =>
    {
        var page = PageWithPhotos(Finds(_scene.Pair("small.jpg", 4096), _scene.Pair("large.jpg", 16384)));
        await page.SearchCommand.ExecuteAsync(null);
        CsvChosen = Path.Combine(_temp.Path, "results.csv");

        await page.SaveCsvCommand.ExecuteAsync(null);

        var rows = File.ReadAllLines(CsvChosen).Skip(1).Select(row => row.Split(',')).ToArray();
        Assert.Equal(
            [("1", "large.jpg"), ("1", "large.jpg"), ("2", "small.jpg"), ("2", "small.jpg")],
            rows.Select(row => (row[0], Path.GetFileName(row[1]))));
        Assert.Equal(page.Groups.SelectMany(row => row.Copies).Select(copy => copy.Copy.Path), rows.Select(row => row[1]));
        Assert.Equal("Saved 2 groups and 4 copies as results.csv.", page.CsvOutcome);

        // A new search's list is not the one saved, so the line about the save goes with the old list.
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Empty(page.CsvOutcome);
    });

    /// <summary>
    /// Nothing can be saved before a search has found a group, or while one runs, since the list is
    /// then part of the results; a dialog the user cancels saves nothing and says nothing.
    /// </summary>
    [Fact]
    public void SavingWaitsForASearchWithResultsAndACancelledDialogSavesNothing() => UiThread.Run(async () =>
    {
        var release = new TaskCompletionSource();
        var group = _scene.Pair("a.jpg", 4096);
        var page = PageWithPhotos(async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(group);
            await Task.Run(() => marksMade(_scene.Marks(candidates)), ct);
            finding.Report(candidates);
            found.Report(group);

            // Held with a group shown, so only the running search can keep the results from being saved.
            await release.Task;

            return new DuplicateSearchResult(candidates, [group], default, Stopped: false);
        });

        Assert.False(page.SaveCsvCommand.CanExecute(null));

        var searching = page.SearchCommand.ExecuteAsync(null);

        while (page.Groups.Count == 0)
        {
            await Task.Yield();
        }

        Assert.True(page.IsSearching);
        Assert.False(page.SaveCsvCommand.CanExecute(null));
        release.SetResult();
        await searching;
        Assert.True(page.SaveCsvCommand.CanExecute(null));

        CsvChosen = null;
        await page.SaveCsvCommand.ExecuteAsync(null);

        Assert.Empty(page.CsvOutcome);
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*.csv", SearchOption.AllDirectories));
    });

    /// <summary>
    /// A search started while the save dialog is open replaces the list the user asked to save, so
    /// nothing is written, and the page says why rather than saving part of the new results.
    /// </summary>
    [Fact]
    public void ASearchStartedWhileTheDialogIsOpenStopsTheSave() => UiThread.Run(async () =>
    {
        var release = new TaskCompletionSource();
        var finds = Finds(_scene.Pair("a.jpg", 4096));
        var searches = 0;
        var page = PageWithPhotos(async (search, marksMade, finding, found, progress, ct) =>
        {
            if (++searches > 1)
            {
                await release.Task;
            }

            return await finds(search, marksMade, finding, found, progress, ct);
        });
        await page.SearchCommand.ExecuteAsync(null);
        var saved = Path.Combine(_temp.Path, "results.csv");
        Task? searching = null;
        ChooseCsv = () =>
        {
            searching = page.SearchCommand.ExecuteAsync(null);
            return Task.FromResult<string?>(saved);
        };

        await page.SaveCsvCommand.ExecuteAsync(null);

        Assert.True(page.IsSearching);
        Assert.False(File.Exists(saved));
        Assert.Equal("The results were not saved, because a search started while the file was being chosen.", page.CsvOutcome);
        release.SetResult();
        await searching!;
    });
}
