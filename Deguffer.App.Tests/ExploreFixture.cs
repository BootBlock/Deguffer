using Deguffer.App.ViewModels;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
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

    public ManualTimeProvider Time { get; } = new();

    public FakeExploreConfirmation Prompt { get; set; } = new(answer: true);

    /// <summary>How the removal policy is built. A finished policy that refuses nothing, unless a test says otherwise.</summary>
    public Func<CancellationToken, Task<ExploreActionPolicy>> Build { get; set; }

    /// <summary>Every relaunch the page asked for, in order.</summary>
    public List<ExploreRequest> Relaunches { get; } = [];

    /// <summary>Whether a relaunch the page asks for starts, which is the user accepting the UAC prompt.</summary>
    public bool RelaunchStarts { get; set; }

    public ExploreViewModel Page(bool isElevated = false) =>
        new(
            Scanner,
            Volumes,
            Time,
            new ExploreActions(Build, () => Prompt, Faults, new FakeRecycleBin()),
            Guide,
            isElevated,
            request =>
            {
                Relaunches.Add(request);
                return RelaunchStarts;
            });

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

    public void Dispose() => Temp.Dispose();

    public static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    public static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, Size: size);
}
