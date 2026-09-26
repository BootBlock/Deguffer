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
    public void DecliningTheStepProtectsEveryDirectoryItWouldHaveCleared()
    {
        var narrowed = Planning(Installation()).NarrowedTo([]);

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
    /// Windows clears the directories whole, so a guard that holds anything back cannot be honoured by
    /// doing less. The executor looks on the disk itself rather than trusting the plan to have
    /// withdrawn the step, and Windows is never asked.
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

        var result = await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, CancellationToken.None);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains("changed in the last", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handlers.Calls);
    }
}
