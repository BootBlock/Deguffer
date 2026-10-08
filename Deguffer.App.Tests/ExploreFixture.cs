using Deguffer.App.ViewModels;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Explore page's view-model stood up on fakes: invented volumes, a scanner the test drives by
/// hand, a policy built from a region table, and a profile rooted in a temp directory. Nothing here
/// reads the machine's volumes, starts a process, or raises the UAC prompt.
/// </summary>
internal sealed class ExploreFixture : IDisposable
{
    public ExploreFixture()
    {
        Environment = new FakeUserEnvironment(Temp.Path);
        Faults = new CrashLog(Environment);
        Guide = ItemGuide.For(new FakeSystemDirectories(Temp.Path), Environment, Volumes);
        Build = _ => Task.FromResult(Policy());
    }

    public TempDirectory Temp { get; } = new();

    public FakeUserEnvironment Environment { get; }

    public CrashLog Faults { get; }

    public ItemGuide Guide { get; }

    public FakeVolumeInventory Volumes { get; } = new();

    public FakeExploreScanner Scanner { get; } = new();

    public FakeHiddenSpaceSource Hidden { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public FakeExploreConfirmation Prompt { get; set; } = new(answer: true);

    /// <summary>How the removal policy is built. A finished policy that refuses nothing, unless a test says otherwise.</summary>
    public Func<CancellationToken, Task<ExploreActionPolicy>> Build { get; set; }

    /// <summary>Every relaunch the page asked for, in order.</summary>
    public List<ExploreRequest> Relaunches { get; } = [];

    /// <summary>What is running on every page, which a test can add to as another page would.</summary>
    public RunningActions Running { get; } = new();

    /// <summary>Whether a relaunch the page asks for starts, which is the user accepting the UAC prompt.</summary>
    public bool RelaunchStarts { get; set; }

    public ExploreViewModel Page(bool isElevated = false) =>
        new(
            Scanner,
            Volumes,
            Hidden,
            Time,
            new ExploreActions(Build, () => Prompt, Faults, Running, new FakeRecycleBin()),
            Guide,
            isElevated,
            request =>
            {
                Relaunches.Add(request);
                return RelaunchStarts;
            },
            Running,
            History);

    /// <summary>The kept scan summaries, stored under the fake profile's own local data.</summary>
    public ScanHistory History => _history ??= new ScanHistory(new ScanHistoryStore(Environment));

    private ScanHistory? _history;

    /// <summary>A policy that refuses each of <paramref name="refusing"/>, and everything under it, with <paramref name="reason"/>.</summary>
    public ExploreActionPolicy Policy(string reason = "Kept by a test.", params string[] refusing) =>
        new(
            [.. refusing.Select(path => ProtectedRegion.Refusing(path, RegionScope.PathAndBelow, reason))],
            [],
            Volumes);

    /// <summary>Scan what the page is pointed at, and finish with <paramref name="scan"/>.</summary>
    public async Task ScanAsync(ExploreViewModel page, ExploreScan scan)
    {
        var running = page.ScanCommand.ExecuteAsync(null);

        Scanner.Current.Finish(scan);

        await running;
    }

    /// <summary>A folder holding one file, beside a file, on the disk so a removal has something to remove.</summary>
    public (ExploreTree Tree, int Folder) OnDisk()
    {
        var root = Temp.CreateDirectory("scan");
        Temp.CreateFile(10, "scan", "old", "a.bin");
        Temp.CreateFile(5, "scan", "keep.bin");

        var builder = new ExploreTreeBuilder(root);
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            Folder("old"),
            File("keep.bin", 5),
        ]);

        builder.AddChildren(folder, [File("a.bin", 10)]);

        return (builder.Build(ExploreChildOrder.BySize), folder);
    }

    public void Dispose() => Temp.Dispose();

    public static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    public static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, Size: size);
}
