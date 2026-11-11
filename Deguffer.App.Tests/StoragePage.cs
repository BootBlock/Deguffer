using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Health;
using Deguffer.Core.Scanning;
using Deguffer.Core.Working;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's view-model as the page builds it, over a profile, a volume and a set of
/// providers that exist only for the test. Nothing here reads this machine's token, its disks or its
/// stored choices.
/// </summary>
internal sealed class StoragePage : IDisposable
{
    public const long Capacity = 1_000_000;

    private readonly TempDirectory _temp = new();

    /// <param name="storedKeepList">
    /// What the keep list file holds before the page starts, or null for none: a first run.
    /// </param>
    /// <param name="selectionStore">
    /// Where the remembered ticks are kept, given the page's own profile, or null for the store the app uses.
    /// </param>
    /// <param name="volumeName">
    /// The <c>\\?\Volume{GUID}\</c> name of the page's one volume, or null for none. A retrim is offered
    /// only on a volume Windows names.
    /// </param>
    public StoragePage(
        IReadOnlyList<FakeCleanupProvider> providers,
        bool isElevated = false,
        long freeBytes = 400_000,
        string? storedKeepList = null,
        Func<FakeUserEnvironment, ISelectionStore>? selectionStore = null,
        string? volumeName = null)
    {
        Environment = new FakeUserEnvironment(_temp.Path);
        VolumeRoot = Path.TrimEndingDirectorySeparator(_temp.Path) + Path.DirectorySeparatorChar;
        Volumes = new FakeVolumeInventory().With(VolumeRoot, totalBytes: Capacity, freeBytes: freeBytes, volumeName: volumeName);
        ToolRoot = _temp.CreateDirectory("profile", "AppData", "Local", "Fake");

        if (storedKeepList is not null)
        {
            File.WriteAllText(Path.Combine(_temp.CreateDirectory("profile", "AppData", "Local", "Deguffer"), "keep.json"), storedKeepList);
        }

        Keeps = new KeepService(new KeepStore(Environment));
        Selections = new SelectionService(selectionStore?.Invoke(Environment) ?? new SelectionStore(Environment));
        Preferences = new PreferenceService(new PreferenceStore(Environment));
        Sizes = new ProviderSizeHistory(new ProviderSizeStore(Environment));

        var planner = new CleanupPlanner(providers);
        ViewModel = new CleanViewModel(
            planner,
            Environment,
            Volumes,
            isElevated,
            request =>
            {
                Relaunches.Add(request);
                return true;
            },
            Selections,
            Keeps,
            Running,
            Work,
            () =>
            {
                PromptsBuilt++;
                return Prompt;
            },
            appVersion: "0.0.0-test",
            new WhenCompleteViewModel(Preferences, Session),
            new RetrimViewModel(
                Volumes,
                Storage,
                Retrim,
                isElevated,
                request =>
                {
                    Relaunches.Add(request);
                    return RelaunchAccepted;
                },
                Running),
            new ScanChoiceViewModel(planner.Providers, Preferences),
            Sizes,
            DriveHealth);
    }

    public CleanViewModel ViewModel { get; }

    public FakeUserEnvironment Environment { get; }

    public FakeVolumeInventory Volumes { get; }

    public string VolumeRoot { get; }

    public KeepService Keeps { get; }

    public SelectionService Selections { get; }

    /// <summary>Each provider's figure at each scan, in this test's own profile.</summary>
    public ProviderSizeHistory Sizes { get; }

    /// <summary>What Health last read of each volume's drives, which a test writes as Health would.</summary>
    public HealthByVolume DriveHealth { get; } = new();

    /// <summary>The stored settings, in this test's own profile.</summary>
    public PreferenceService Preferences { get; }

    /// <summary>The storage behind the page's volume, which decides whether a retrim is offered on it.</summary>
    public FakeStorageQueries Storage { get; } = new();

    /// <summary>Windows' retrim, which records what it is asked and retrims nothing.</summary>
    public FakeVolumeRetrim Retrim { get; } = new();

    /// <summary>Whether the user accepts Windows' prompt when the retrim offer restarts Deguffer as administrator.</summary>
    public bool RelaunchAccepted { get; set; } = true;

    /// <summary>The Windows session a finished clean may lock, log off, sleep or shut down.</summary>
    public FakeWindowsSession Session { get; } = new();

    /// <summary>What is running on every page, which a test can add to as another page would.</summary>
    public RunningActions Running { get; } = new();

    /// <summary>What is under way on every page, which the taskbar button shows.</summary>
    public BusyWork Work { get; } = new();

    public ScriptedPrompt Prompt { get; } = new();

    /// <summary>How many times the page built its confirmation dialog. It builds one only when it has something to ask.</summary>
    public int PromptsBuilt { get; private set; }

    /// <summary>The folder the fake tool keeps its caches in, which a clean must never take (§5.2).</summary>
    public string ToolRoot { get; }

    /// <summary>
    /// A cache folder inside <see cref="ToolRoot"/> holding one file, and the step that removes it.
    /// </summary>
    public DeleteDirectoryStep Cache(string name, long bytes, string? key = null)
    {
        var path = Path.Combine(ToolRoot, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[4]);

        return new DeleteDirectoryStep(path, name)
        {
            Estimated = ScanSize.FromLengths(bytes),
            Identity = key is null ? null : new ItemIdentity(key, name),
        };
    }

    /// <summary>What the volume says it has left from now on, as it would after something wrote to it or freed space on it.</summary>
    public void FreeSpaceBecomes(long freeBytes) =>
        Volumes.Without(VolumeRoot).With(VolumeRoot, totalBytes: Capacity, freeBytes: freeBytes);

    /// <summary>
    /// Make the next scan's providers finish in <paramref name="order"/>, each one held until the row
    /// of the one before it has landed on the page, so the rows land in that order however busy the
    /// machine is.
    /// </summary>
    public void FinishIn(params IReadOnlyList<FakeCleanupProvider> order)
    {
        var finishing = new FinishingOrder(order);

        ViewModel.Findings.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<FindingViewModel>() ?? [])
            {
                finishing.Report(row.Finding);
            }
        };
    }

    public void Scan() => UiThread.Run(() => ViewModel.PreviewCommand.ExecuteAsync(null));

    /// <summary>Scan only the locations chosen in the Scan button's drop-down.</summary>
    public void ScanChosen() => UiThread.Run(() => ViewModel.PreviewChosenCommand.ExecuteAsync(null));

    /// <summary>Every elevated relaunch the page asked for, in order. Each reports that it started.</summary>
    public List<ElevationRequest> Relaunches { get; } = [];

    public void Clean() => UiThread.Run(() => ViewModel.CleanCommand.ExecuteAsync(null));

    public FindingViewModel Row(string providerId) =>
        ViewModel.Findings.Single(row => row.Finding.Provider.Id == providerId);

    public void Dispose()
    {
        // The last tick's write runs on a worker, and must not land in a folder being deleted.
        Selections.Flush();
        _temp.Dispose();
    }
}
