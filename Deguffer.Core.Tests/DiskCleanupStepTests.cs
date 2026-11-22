using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A step Windows carries out through one of its Disk Cleanup handlers, as the plan and the run see
/// it: every directory the handler clears is a target, a declined one leaves every one of them
/// protected, an Outlook data file inside any of them withholds it, and a run holding one has no
/// bounded reach.
///
/// <para>Driven against plans built here, because each of these is a rule of the plan's or the run's
/// rather than of any provider. The providers' own tests drive the same rules end to end.</para>
/// </summary>
public sealed class DiskCleanupStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Every path is inside the scratch tree, so nothing here can reach the drive the suite runs on
    /// even where a rule under test has been broken on purpose.
    /// </summary>
    private string Volume => _temp.Path;

    private string Staging => Path.Combine(Volume, "$Windows.~WS");

    private string Download => Path.Combine(Volume, "ESD", "Download");

    private string Store => Path.Combine(Download, "archive.pst");

    private DiskCleanupStep Installation() => new(Staging, "Installation files")
    {
        Handler = "Windows ESD installation files",
        Volume = Volume,
        AlsoClears = [Download],
        Estimated = new ScanSize(8192, 8192),
    };

    private static CleanupPlan Planning(params CleanupStep[] steps) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableWithCost,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
    };

    /// <summary>
    /// One row, and Windows clears every directory its registration names. §5.6 reads
    /// <see cref="CleanupPlan.TargetedPaths"/> to tell Deguffer's own removals from a stranger's, so
    /// a directory missing from it would be an alarm about Windows doing what it was asked.
    /// </summary>
    [Fact]
    public void EveryDirectoryTheHandlerClearsIsATarget()
    {
        Assert.Equal([Staging, Download], Planning(Installation()).TargetedPaths);
    }

    /// <summary>
    /// A declined handler step leaves every directory it would have cleared standing, and §5.6 has to
    /// be told so about each of them, not only the one the row is named after.
    /// </summary>
    [Fact]
    public async Task DecliningTheStepProtectsEveryDirectoryItWouldHaveCleared()
    {
        var narrowed = await Planning(Installation()).NarrowedToAsync([]);

        Assert.Empty(narrowed.Steps);
        Assert.Equal(
            [Staging, Download],
            narrowed.ProtectedPaths.Select(p => p.Path));
        Assert.All(narrowed.ProtectedPaths, p => Assert.Equal(PathPresence.Present, p.PresenceBefore));
    }

    /// <summary>
    /// Windows' handler decides for itself what goes with <c>Windows.old</c>, so a run holding one has
    /// no reach Deguffer can state — the same as a run holding a tool's own command.
    /// </summary>
    [Fact]
    public void ARunHoldingAHandlerStepHasNoBoundedReach()
    {
        var reach = RunReach.Of([Planning(Installation())]);

        Assert.True(reach.Unbounded);
        Assert.Equal([Staging, Download], reach.TargetedPaths);
    }

    [Fact]
    public void ARunWithoutOneKeepsItsBoundedReach()
    {
        Assert.False(RunReach.Of([Planning(new DeleteDirectoryStep(Staging, "Files"))]).Unbounded);
    }

    /// <summary>
    /// §9: Windows clears the directories whole and cannot be told to leave one file, so a store in
    /// any of them withholds the step. The directory holding the store is protected on its contents,
    /// and the one that held none is not claimed to have held anything.
    /// </summary>
    [Fact]
    public void AStoreInAnyDirectoryWithholdsTheStepAndProtectsTheDirectoryHoldingIt()
    {
        var applied = MailStorePlan.Apply(Planning(Installation() with { MailStores = [Store] }));

        Assert.Empty(applied.Steps);
        Assert.Contains(applied.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(Store, StringComparison.Ordinal));

        var whole = Assert.Single(applied.ProtectedPaths, p => p.Withheld == Withholding.None);
        Assert.Equal(Download, whole.Path);
        Assert.True(whole.HeldContentBefore);

        Assert.Contains(applied.ProtectedPaths, p => p.Path == Store && p.Withheld == Withholding.MailStore);
    }

    /// <summary>
    /// The look on the disk before Windows clears the directories whole is the step's look, so a slow
    /// one is reported as a look rather than as Windows' cleanup.
    /// </summary>
    [Fact]
    public async Task TheLookBeforeWindowsClearsIsTimedAsALook()
    {
        var handlers = FakeDiskCleanupHandlers.DoingNothing();
        var executor = new PlanExecutor(
            new FakeProcessRunner(),
            ParallelEnumerationScanner.Default,
            RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
            handlers: handlers);

        Directory.CreateDirectory(Download);
        File.WriteAllBytes(Path.Combine(Download, "update.cab"), new byte[16]);

        var result = await executor.ExecuteAsync(Planning(Installation()), runReach: null, residue: null, progress: null, underway: null, CancellationToken.None);
        var timing = Assert.Single(result.Steps).Timing;

        Assert.Single(handlers.Purged);
        Assert.True(timing.Look > TimeSpan.Zero, "the look before Windows' cleanup was not timed");
        Assert.True(timing.Look <= timing.Elapsed, "the look took longer than its step");
    }

    /// <summary>
    /// Windows clears the directories whole, so a guard that holds anything back cannot be honoured by
    /// doing less. The executor looks on the disk itself rather than trusting the plan to have
    /// withdrawn the step, and Windows is never let purge.
    /// </summary>
    [Fact]
    public async Task TheRunRefusesAStepWhoseRecentFilesTheGuardWouldKeep()
    {
        var handlers = FakeDiskCleanupHandlers.DoingNothing();
        var executor = new PlanExecutor(
            new FakeProcessRunner(),
            ParallelEnumerationScanner.Default,
            RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
            handlers: handlers);

        Directory.CreateDirectory(Download);
        File.WriteAllBytes(Path.Combine(Download, "recent.bin"), new byte[16]);

        var plan = Planning(Installation()) with
        {
            Keep = MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow),
        };

        var result = await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, underway: null, CancellationToken.None);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains("changed in the last", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handlers.Purged);
        Assert.True(Directory.Exists(Download), "a refused step's directory was cleared");
    }

    /// <summary>
    /// Windows walks the directories to measure them before it purges, and the look walks them too, so
    /// the handler measures while the look still decides whether it may purge. The look is held in the
    /// middle of its walk until the handler has started measuring, so a step that hosted the handler
    /// only after its look would never let the look finish. Once the look passes, the handler clears
    /// exactly once.
    /// </summary>
    [Fact]
    public async Task TheHandlerMeasuresWhileTheLookDecidesAndPurgesOnceItPasses()
    {
        var measuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = FakeDiskCleanupHandlers.Windows(measurement: _ => measuring.TrySetResult());
        var measuredDuringTheLook = false;

        var fs = new ListingHookFileSystem(
            WindowsFileSystem.Default,
            Download,
            () => measuredDuringTheLook = measuring.Task.Wait(TimeSpan.FromSeconds(10)));

        var result = await Executing(handlers, Planning(Installation()), fs);

        Assert.True(measuredDuringTheLook, "the handler did not measure while the look was walking");
        Assert.True(Assert.Single(result.Steps).Succeeded);
        Assert.Equal([("Windows ESD installation files", Volume)], handlers.Purged);

        // §5.6: the volume the handler was given, and everything on it it does not clear, survived.
        Assert.False(Directory.Exists(Staging));
        Assert.False(Directory.Exists(Download));
        Assert.True(File.Exists(Sibling));
    }

    /// <summary>
    /// A look that refuses lets the handler go without purging, and the step waits for it to return
    /// first. Told to stop, a handler may still finish measuring before it returns, and the next step
    /// could start a second instance of the same handler beside it.
    /// </summary>
    [Fact]
    public async Task ARefusedLookLetsTheMeasuringHandlerGoAndWaitsForIt()
    {
        var handlers = FakeDiskCleanupHandlers.Windows(measurement: ReturningAWhileAfterItIsStopped);

        var plan = Planning(Installation()) with
        {
            Keep = MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow),
        };

        // Every file is written now, so each one is within the week the guard keeps.
        var result = await Executing(handlers, plan);

        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.Empty(handlers.Purged);
        Assert.Equal(["Windows ESD installation files"], handlers.RunsReturned);
        Assert.True(Directory.Exists(Staging), "a refused step's directory was cleared");
        Assert.True(Directory.Exists(Download), "a refused step's directory was cleared");
        Assert.True(File.Exists(Sibling));
    }

    /// <summary>
    /// A clean cancelled in the middle of the look ends the step without a look that passed, so the
    /// handler is let go without purging and waited for, as it is for a refusal.
    /// </summary>
    [Fact]
    public async Task ALookCancelledPartWayLetsTheMeasuringHandlerGoAndWaitsForIt()
    {
        var handlers = FakeDiskCleanupHandlers.Windows(measurement: ReturningAWhileAfterItIsStopped);
        using var cancel = new CancellationTokenSource();
        var fs = new ListingHookFileSystem(WindowsFileSystem.Default, Download, cancel.Cancel);

        var failure = await Record.ExceptionAsync(() => Executing(handlers, Planning(Installation()), fs, cancel.Token));

        Assert.True(failure is null or OperationCanceledException, $"the cancelled clean failed with {failure}");
        Assert.Empty(handlers.Purged);
        Assert.Equal(["Windows ESD installation files"], handlers.RunsReturned);
        Assert.True(Directory.Exists(Staging), "a cancelled step's directory was cleared");
        Assert.True(Directory.Exists(Download), "a cancelled step's directory was cleared");
        Assert.True(File.Exists(Sibling));
    }

    /// <summary>
    /// Measures until it is told to stop, and returns a while after, as a real handler does. Bounded,
    /// so a step that never told it to stop fails the test rather than hanging it.
    /// </summary>
    private static void ReturningAWhileAfterItIsStopped(CancellationToken ct)
    {
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        Thread.Sleep(200);
    }

    private string Sibling => Path.Combine(Volume, "Users", "keep.txt");

    /// <summary>
    /// Runs <paramref name="plan"/> over the scratch volume, with both directories and an unrelated
    /// file in place, through <paramref name="fs"/> where a test holds or cancels the look.
    /// </summary>
    private async Task<CleanupResult> Executing(
        FakeDiskCleanupHandlers handlers,
        CleanupPlan plan,
        IFileSystem? fs = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "install.esd"), new byte[16]);
        Directory.CreateDirectory(Download);
        File.WriteAllBytes(Path.Combine(Download, "update.cab"), new byte[16]);
        Directory.CreateDirectory(Path.GetDirectoryName(Sibling)!);
        File.WriteAllBytes(Sibling, new byte[16]);

        var executor = new PlanExecutor(
            new FakeProcessRunner(),
            ParallelEnumerationScanner.Default,
            RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
            handlers: handlers,
            fileSystem: fs);

        return await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, underway: null, ct);
    }
}
