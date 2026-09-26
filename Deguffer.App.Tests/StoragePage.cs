using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
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
    public StoragePage(
        IReadOnlyList<FakeCleanupProvider> providers,
        bool isElevated = false,
        long freeBytes = 400_000,
        string? storedKeepList = null)
    {
        Environment = new FakeUserEnvironment(_temp.Path);
        VolumeRoot = Path.TrimEndingDirectorySeparator(_temp.Path) + Path.DirectorySeparatorChar;
        Volumes = new FakeVolumeInventory().With(VolumeRoot, totalBytes: Capacity, freeBytes: freeBytes);
        ToolRoot = _temp.CreateDirectory("profile", "AppData", "Local", "Fake");

        if (storedKeepList is not null)
        {
            File.WriteAllText(Path.Combine(_temp.CreateDirectory("profile", "AppData", "Local", "Deguffer"), "keep.json"), storedKeepList);
        }

        Keeps = new KeepService(new KeepStore(Environment));
        Selections = new SelectionService(new SelectionStore(Environment));

        ViewModel = new CleanViewModel(
            new CleanupPlanner(providers),
            Environment,
            Volumes,
            isElevated,
            Selections,
            Keeps,
            () =>
            {
                PromptsBuilt++;
                return Prompt;
            });
    }

    public CleanViewModel ViewModel { get; }

    public FakeUserEnvironment Environment { get; }

    public FakeVolumeInventory Volumes { get; }

    public string VolumeRoot { get; }

    public KeepService Keeps { get; }

    public SelectionService Selections { get; }

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

    public void Scan() => UiThread.Run(() => ViewModel.PreviewCommand.ExecuteAsync(null));

    public void Clean() => UiThread.Run(() => ViewModel.CleanCommand.ExecuteAsync(null));

    public FindingViewModel Row(string providerId) =>
        ViewModel.Findings.Single(row => row.Finding.Provider.Id == providerId);

    public void Dispose() => _temp.Dispose();
}
