using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Safety;
using Deguffer.Core.Working;
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
        _disk = new ConfinedFileSystem(WindowsFileSystem.Default, Temp.Path);
        Build = _ => Task.FromResult(Policy());
    }

    /// <summary>What every <see cref="Policy"/> reads: the disk inside <see cref="Temp"/>, and nothing elsewhere.</summary>
    private readonly ConfinedFileSystem _disk;

    public TempDirectory Temp { get; } = new();

    public FakeUserEnvironment Environment { get; }

    public CrashLog Faults { get; }

    public ItemGuide Guide { get; }

    public FakeVolumeInventory Volumes { get; } = new();

    public FakeExploreScanner Scanner { get; } = new();

    public FakeHiddenSpaceSource Hidden { get; } = new();

    /// <summary>What a location typed into the page finds: nothing, unless a test describes it.</summary>
    public FakePathProbe Probe { get; } = new();

    /// <summary>What a page built after this is set asks instead of <see cref="Probe"/>, for a test that holds the answer back.</summary>
    public IPathProbe? HeldProbe { get; set; }

    public ManualTimeProvider Time { get; } = new();

    public FakeExploreConfirmation Prompt { get; set; } = new(answer: true);

    /// <summary>The Recycle Bin a page built after this is set removes to.</summary>
    public FakeRecycleBin Bin { get; set; } = new();

    /// <summary>How the removal policy is built. A finished policy that refuses nothing, unless a test says otherwise.</summary>
    public Func<CancellationToken, Task<ExploreActionPolicy>> Build { get; set; }

    /// <summary>The scope of every policy build the page started, in order.</summary>
    public List<ProtectionScope> Scopes { get; } = [];

    /// <summary><see cref="Build"/>, as the actions call it, recording each scope in <see cref="Scopes"/>.</summary>
    public Func<ProtectionScope, CancellationToken, Task<ExploreActionPolicy>> Building => (scope, ct) =>
    {
        Scopes.Add(scope);
        return Build(ct);
    };

    /// <summary>Every relaunch the page asked for, in order.</summary>
    public List<ExploreRequest> Relaunches { get; } = [];

    /// <summary>What is running on every page, which a test can add to as another page would.</summary>
    public RunningActions Running { get; } = new();

    /// <summary>What is under way on every page, which the taskbar button shows.</summary>
    public BusyWork Work { get; } = new();

    /// <summary>Whether a relaunch the page asks for starts, which is the user accepting the UAC prompt.</summary>
    public bool RelaunchStarts { get; set; }

    /// <summary>
    /// A page as the window shows it: built, then reading its drives as it is navigated to, which
    /// the fake inventory answers at once.
    /// </summary>
    public ExploreViewModel Page(bool isElevated = false)
    {
        var page = Built(isElevated);
        var shown = page.Targeting.ShownAsync(null, CancellationToken.None);

        Assert.True(shown.IsCompleted, "The fake inventory answers at once, so the first reading finishes as the page is shown.");
        shown.GetAwaiter().GetResult();

        return page;
    }

    /// <summary>
    /// A page shown in the List layout, for a test of the folder's rows: the maps never read them, so
    /// they are written only while the list is on screen.
    /// </summary>
    public ExploreViewModel Listed()
    {
        var page = Page();

        page.SelectedView = ExploreView.List;

        return page;
    }

    /// <summary>A page built and not yet shown, so nothing has read its drives.</summary>
    public ExploreViewModel Built(bool isElevated = false) =>
        new(
            Scanner,
            Volumes,
            Hidden,
            HeldProbe ?? Probe,
            Time,
            new ExploreActions(Building, () => Prompt, Faults, Running, Bin),
            Guide,
            isElevated,
            request =>
            {
                Relaunches.Add(request);
                return RelaunchStarts;
            },
            Running,
            Work,
            History,
            Motion,
            () =>
            {
                Releases++;
                Releasing?.Invoke();
            });

    /// <summary>How many times a page built here asked to free what its scans' snapshots left behind.</summary>
    public int Releases { get; private set; }

    /// <summary>Run as a page built here asks to free them, to see what the page is doing at that moment.</summary>
    public Action? Releasing { get; set; }

    /// <summary>The reader's Animation effects setting, as the page's replay follows it.</summary>
    public ScriptedMotion Motion { get; } = new(animationsEnabled: true);

    /// <summary>The kept scan summaries, stored under the fake profile's own local data.</summary>
    public ScanHistory History => _history ??= new ScanHistory(new ScanHistoryStore(Environment));

    private ScanHistory? _history;

    /// <summary>
    /// A policy that refuses each of <paramref name="refusing"/>, and everything under it, with
    /// <paramref name="reason"/>. It reads the disk only inside <see cref="Temp"/>, where a test builds
    /// a real tree, and finds nothing anywhere else: most trees a test hands the page are invented at
    /// letters such as <c>C:\</c>, and the policy would otherwise ask the machine's own drive there
    /// where each folder leads.
    /// </summary>
    public ExploreActionPolicy Policy(string reason = "Kept by a test.", params string[] refusing) =>
        new(
            [.. refusing.Select(path => ProtectedRegion.Refusing(path, RegionScope.PathAndBelow, reason))],
            [],
            Volumes,
            _disk);

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

    /// <summary>A file created and last written at <paramref name="when"/>.</summary>
    public static ExploreChild Written(string name, long size, DateTime when) =>
        new(name, IsDirectory: false, IsLink: false, size, ExploreTimestamp.FromUtc(when), ExploreTimestamp.FromUtc(when));
}
