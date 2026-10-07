using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Uninstalling the programs the user checked (§7.3): one at a time, in order, asking before each
/// one after the first, and starting no more once the user stops the queue.
/// </summary>
public sealed class UninstallQueueTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakePathProbe _paths = new();

    private readonly FakeUninstallLauncher _launcher = new();

    private readonly ScriptedInstalledAppsPrompt _prompt = new(answer: true);

    public void Dispose() => _temp.Dispose();

    private InstalledAppsReader Reader => new(_registry, new FakeWindowsInstaller(), _paths, FixedSystemDirectories.Standard, new FakePackageDependencies(), new FakeVolumeInventory());

    private ProgramUninstaller Uninstaller => new(Reader, _launcher, @"C:\Windows\System32\msiexec.exe");

    private UninstallQueue Queue => new(Uninstaller, () => _prompt);

    private PreparedUninstall Installed(string name)
    {
        var uninstaller = Path.Combine(_temp.Path, name, "unins000.exe");
        _paths.File(uninstaller);
        var key = _registry.With(UninstallScope.CurrentUser, name, ("DisplayName", name), ("UninstallString", $"\"{uninstaller}\""));
        return Uninstaller.Prepare(Reader.ReadAgain(key)!).Prepared!;
    }

    private string[] Started => [.. _launcher.Started.Select(l => Path.GetFileName(Path.GetDirectoryName(l.FileName))!)];

    [Fact]
    public async Task EachUninstallerRunsInOrderAndTheNextIsAskedFor()
    {
        var queue = new[] { Installed("A"), Installed("B"), Installed("C") };
        var steps = new List<UninstallStep>();

        var report = await Queue.RunAsync(queue, new CallbackProgress<UninstallStep>(steps.Add), CancellationToken.None);

        Assert.Equal(["A", "B", "C"], Started);
        Assert.Equal(["Uninstall 'B' next?", "Uninstall 'C' next?"], _prompt.Prompts.Select(p => p.Title));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], steps.Select(s => (s.Number, s.Count)));
        Assert.Equal(3, report.Finished.Count);
        Assert.True(report.IsComplete);
    }

    /// <summary>The question comes after the last one exited, so it can say what the list now says.</summary>
    [Fact]
    public async Task TheQuestionCarriesWhatTheLastUninstallLeft()
    {
        var first = Installed("A");
        _launcher.WhileRunning = _ => _registry.Remove(first.Entry.Key);

        await Queue.RunAsync([first, Installed("B")], new CallbackProgress<UninstallStep>(_ => { }), CancellationToken.None);

        Assert.Contains("'A' is no longer in the list", Assert.Single(_prompt.Prompts).Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheNextStartsNoMore()
    {
        _prompt.Answer = false;

        var report = await Queue.RunAsync([Installed("A"), Installed("B"), Installed("C")], new CallbackProgress<UninstallStep>(_ => { }), CancellationToken.None);

        Assert.Equal(["A"], Started);
        Assert.Equal(1, _prompt.Asked);
        Assert.Equal(["B", "C"], report.NotRun.Select(e => e.Name));
        Assert.False(report.IsComplete);
        Assert.Contains("The uninstallers of 2 programs were not started: 'B', 'C'.", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>Cancel stops the wait, never the uninstaller, and the report says it may still run.</summary>
    [Fact]
    public async Task CancellingWhileWatchingAbandonsTheWaitAndStartsNoMore()
    {
        using var cancel = new CancellationTokenSource();
        _launcher.RunsUntilCancelled = true;
        _launcher.WhileRunning = _ => cancel.Cancel();

        var report = await Queue.RunAsync([Installed("A"), Installed("B")], new CallbackProgress<UninstallStep>(_ => { }), cancel.Token);

        Assert.Equal(["A"], Started);
        Assert.Equal("A", report.Abandoned?.Name);
        Assert.Equal(["B"], report.NotRun.Select(e => e.Name));
        Assert.Empty(report.Finished);
        Assert.Contains("It may still be running.", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>The question was going to be answered yes, so only the cancel can stop the queue.</summary>
    [Fact]
    public async Task CancellingWhileAskingStartsNoMore()
    {
        using var cancel = new CancellationTokenSource();
        _prompt.WhileAsking = _ => cancel.Cancel();

        var report = await Queue.RunAsync([Installed("A"), Installed("B")], new CallbackProgress<UninstallStep>(_ => { }), cancel.Token);

        Assert.Equal(["A"], Started);
        Assert.Null(report.Abandoned);
        Assert.Equal(["B"], report.NotRun.Select(e => e.Name));
    }

    [Fact]
    public async Task ACancelBeforeTheFirstStartsNothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var report = await Queue.RunAsync([Installed("A")], new CallbackProgress<UninstallStep>(_ => { }), cancel.Token);

        Assert.Empty(_launcher.Started);
        Assert.Null(report.Abandoned);
        Assert.Equal("The uninstaller of 'A' was not started.", report.Summary);
    }

    /// <summary>One program is one action: nothing is asked between uninstallers when there is no second.</summary>
    [Fact]
    public async Task OneProgramAsksNothingFurther()
    {
        await Queue.RunAsync([Installed("A")], new CallbackProgress<UninstallStep>(_ => { }), CancellationToken.None);

        Assert.Equal(0, _prompt.Asked);
    }
}
