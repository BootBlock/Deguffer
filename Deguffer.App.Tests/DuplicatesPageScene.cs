using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Duplicates page as its tests build it: Core's search stood in for by one that hands over the
/// groups it is given as the real one does, and Core's rules and removal run for real on the scratch
/// drive of a <see cref="DuplicateScene"/>, with only the dialog and the Recycle Bin stood in for.
/// </summary>
public abstract class DuplicatesPageScene : IDisposable
{
    private protected readonly TempDirectory _temp = new();
    private protected readonly DuplicateScene _scene = new();
    private protected readonly PreferenceService _preferences;
    private protected readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\").With(@"D:\");
    private protected readonly List<DuplicateSearch> _searches = [];
    private protected readonly List<ElevationRequest> _relaunches = [];
    private protected readonly RunningActions _running = new();

    protected DuplicatesPageScene() =>
        _preferences = new PreferenceService(new PreferenceStore(new FakeUserEnvironment(_temp.Path)));

    /// <summary>What the page's confirmation is answered with; declining unless a test says otherwise.</summary>
    private protected FakeDuplicateConfirmation Prompt { get; set; } = new(false);

    /// <summary>The file the save dialog answers with, or null where the user cancels it.</summary>
    private protected string? CsvChosen { get; set; }

    private protected string Photos => _scene.Folder("Photos");

    public void Dispose()
    {
        _scene.Dispose();
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A search that finds <paramref name="groups"/> and hands them over as Core's search does.</summary>
    private protected RunDuplicateSearch Finds(params DuplicateGroup[] groups) => async (search, marksMade, finding, found, progress, ct) =>
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

    private protected DuplicatesViewModel Page(RunDuplicateSearch? run = null, DuplicatesRequest? requested = null) =>
        new(
            run ?? Finds(),

            // Asked through the property, so a test can change the answer after the page is built.
            new DuplicateActions(_scene.ProtectionsAsync, _ => null, () => Prompt, _scene.Remover(), _running),
            () => Task.FromResult(CsvChosen),
            _preferences,
            new DriveList(_volumes, new ManualTimeProvider()),
            isElevated: false,
            request =>
            {
                _relaunches.Add(request);
                return false;
            },
            _running,
            requested);

    /// <summary>A page with one location, ready to search.</summary>
    private protected DuplicatesViewModel PageWithPhotos(RunDuplicateSearch? run = null)
    {
        var page = Page(run);
        page.Locations.AddFolder(Photos);

        return page;
    }

    private protected async Task<DuplicateSearch> SearchedWith(DuplicatesViewModel page)
    {
        await page.SearchCommand.ExecuteAsync(null);

        return _searches[^1];
    }
}
