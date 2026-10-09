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
public sealed class DuplicatesViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DuplicateScene _scene = new();
    private readonly PreferenceService _preferences;
    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\").With(@"D:\");
    private readonly List<DuplicateSearch> _searches = [];
    private readonly List<ElevationRequest> _relaunches = [];

    public DuplicatesViewModelTests() =>
        _preferences = new PreferenceService(new PreferenceStore(new FakeUserEnvironment(_temp.Path)));

    public void Dispose()
    {
        _scene.Dispose();
        _temp.Dispose();
    }

    private string Photos => _scene.Folder("Photos");

    /// <summary>A search that finds <paramref name="groups"/> and hands them over as Core's search does.</summary>
    private RunDuplicateSearch Finds(params DuplicateGroup[] groups) => async (search, marksMade, finding, found, progress, ct) =>
    {
        _searches.Add(search);
        var candidates = DuplicateScene.Finding(groups);

        // Off the page's thread, as the search hands them over.
        await Task.Run(() => marksMade(_scene.Marks(candidates)), ct);
        finding.Report(candidates);

        foreach (var group in groups)
        {
            found.Report(group);
        }

        // After the groups' posts, as the search returns after its last group.
        await Task.Yield();

        return new DuplicateSearchResult(candidates, groups, default, Stopped: false);
    };

    private DuplicatesViewModel Page(RunDuplicateSearch? run = null, DuplicatesRequest? requested = null) =>
        new(
            run ?? Finds(),
            _preferences,
            new DriveList(_volumes, new ManualTimeProvider()),
            isElevated: false,
            request =>
            {
                _relaunches.Add(request);
                return false;
            },
            new RunningActions(),
            requested);

    /// <summary>A page with one location, ready to search.</summary>
    private DuplicatesViewModel PageWithPhotos(RunDuplicateSearch? run = null)
    {
        var page = Page(run);
        page.Locations.AddFolder(Photos);

        return page;
    }

    private async Task<DuplicateSearch> SearchedWith(DuplicatesViewModel page)
    {
        await page.SearchCommand.ExecuteAsync(null);

        return _searches[^1];
    }

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
        Assert.Equal(SearchNoteKind.Stopped, page.Notes[^1].Kind);
        Assert.False(page.IsSearching);
    });
}
