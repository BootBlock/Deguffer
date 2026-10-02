using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A plan the user cancels part-way. §5.6 makes acting and proving what survived one step, so a
/// cancelled plan still returns what it did and is still verified: something has already gone, and
/// the user chose where it stopped without knowing what it had reached.
/// </summary>
public sealed class PlanExecutorCancellationTests : IDisposable
{
    private const int FilesPerFolder = 300;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RefusalRecord RefusalLog => RefusalRecord.For(new FakeUserEnvironment(_temp.Path));

    /// <summary>
    /// The removal is stopped with most of its tree still to go, the next step never starts, and the
    /// tool's own folder (§5.2) and the file beside the targets are proved standing all the same.
    /// </summary>
    [Fact]
    public async Task APlanStoppedPartWayReportsWhatItDidAndIsStillVerified()
    {
        var root = _temp.CreateDirectory("tool");
        var settings = _temp.CreateFile(64, "tool", "settings.json");
        var cache = Tree("tool", "cache");
        var logs = _temp.CreateDirectory("tool", "logs");
        _temp.CreateFile(64, "tool", "logs", "today.log");

        using var cts = new CancellationTokenSource();

        var result = await Executor().ExecuteAsync(
            Plan(
                [new DeleteDirectoryStep(cache, "The cache"), new DeleteDirectoryStep(logs, "The logs")],
                new ProtectedPath(root, "The tool's own folder.", PathPresence.Present),
                new ProtectedPath(settings, "The tool's settings.", PathPresence.Present)),
            runReach: null,
            residue: null,
            CancelOnFirstWork(cts),
            cts.Token);

        Assert.True(result.Interrupted);

        var stopped = Assert.Single(result.Steps);
        Assert.True(stopped.Interrupted);
        Assert.False(stopped.Succeeded);
        Assert.InRange(stopped.BytesReclaimed, 1, (4 * FilesPerFolder * 16) - 1);
        Assert.StartsWith("Stopped part-way when the clean was cancelled", stopped.Message);

        Assert.True(Directory.Exists(logs), "a step the cancelled plan never started was carried out");

        // §5.6's negative, on the disk and in the verdict.
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(settings));

        var verification = Assert.IsType<VerificationResult>(result.Verification);
        Assert.True(verification.Passed);
        Assert.Equal(
            [VerificationOutcome.Survived, VerificationOutcome.Survived],
            verification.Checks.Select(c => c.Outcome));
    }

    /// <summary>
    /// A protected folder inside the tree the removal was stopped in is one it went into, which is
    /// the over-reach §5.6 exists to report. It is still standing, and still holds its folders, so
    /// only the removal's own record of where it went can say so.
    /// </summary>
    [Fact]
    public async Task AProtectedFolderTheStoppedRemovalWentIntoFailsTheRun()
    {
        var cache = Tree("cache");
        var inside = Path.Combine(cache, "d2");
        _temp.CreateDirectory("cache", "d2", "nested");

        using var cts = new CancellationTokenSource();

        var result = await Executor().ExecuteAsync(
            Plan(
                [new DeleteDirectoryStep(cache, "The cache")],
                new ProtectedPath(inside, "Somebody else's folder.", PathPresence.Present)),
            runReach: null,
            residue: null,
            CancelOnFirstWork(cts),
            cts.Token);

        Assert.True(result.Interrupted);

        var check = Assert.Single(result.Verification!.Checks);
        Assert.Equal(VerificationOutcome.Entered, check.Outcome);
        Assert.False(result.Verification.Passed);
    }

    /// <summary>
    /// A step that throws when it is cancelled, as a tool's command does, cannot say what it did. It
    /// is reported as stopped, nothing is counted for it, and the plan is verified.
    /// </summary>
    [Fact]
    public async Task AStepThatThrowsWhenCancelledIsReportedAsStoppedAndThePlanVerified()
    {
        var root = _temp.CreateDirectory("tool");
        using var cts = new CancellationTokenSource();

        var runner = new FakeProcessRunner().Replying("tool", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var result = await new PlanExecutor(runner, ParallelEnumerationScanner.Default, RefusalLog).ExecuteAsync(
            Plan(
                [new RunCommandStep("tool", "clean", "Clean"), new RunCommandStep("tool", "prune", "Prune")],
                new ProtectedPath(root, "The tool's own folder.", PathPresence.Present)),
            runReach: null,
            residue: null,
            progress: null,
            cts.Token);

        Assert.True(result.Interrupted);
        Assert.Single(runner.Invocations);

        var stopped = Assert.Single(result.Steps);
        Assert.True(stopped.Interrupted);
        Assert.Equal(0, stopped.BytesReclaimed);
        Assert.StartsWith("Stopped when the clean was cancelled", stopped.Message);

        Assert.True(result.Verification!.Passed);
        Assert.Equal(VerificationOutcome.Survived, Assert.Single(result.Verification.Checks).Outcome);
    }

    /// <summary>A plan cancelled before its first step runs none of them, and is verified all the same.</summary>
    [Fact]
    public async Task APlanCancelledBeforeItStartsRunsNothingAndIsVerified()
    {
        var root = _temp.CreateDirectory("tool");
        var cache = Tree("tool", "cache");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Executor().ExecuteAsync(
            Plan(
                [new DeleteDirectoryStep(cache, "The cache")],
                new ProtectedPath(root, "The tool's own folder.", PathPresence.Present)),
            runReach: null,
            residue: null,
            progress: null,
            cts.Token);

        Assert.True(result.Interrupted);
        Assert.Empty(result.Steps);
        Assert.Equal(4 * FilesPerFolder, Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Count());
        Assert.True(result.Verification!.Passed);
    }

    /// <summary>
    /// An index removal the clean was cancelled in says it was stopped, rather than that the index
    /// could not be removed, and is not complete, which is what keeps the executor away from the
    /// directory it indexes.
    /// </summary>
    [Fact]
    public async Task AnIndexRemovalStoppedByTheCancelSaysItWasStopped()
    {
        var index = _temp.CreateDirectory("project", "index");
        _temp.CreateFile(16, "project", "index", "entries.bin");
        var output = _temp.CreateDirectory("project", "out");
        var step = new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var removal = await IndexRemoval.RemoveAsync(step, MinimumAge.Off, RefusalLog, new RunResidue(), cts.Token);

        Assert.True(removal.Interrupted);
        Assert.False(removal.Complete);

        var outcome = removal.Stopped(step);
        Assert.True(outcome.Interrupted);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Stopped when the clean was cancelled", outcome.Message);
    }

    private PlanExecutor Executor() =>
        new(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);

    /// <summary>
    /// A tree of four folders holding enough files that a removal reports progress well before its
    /// end, which is where the tests stop it.
    /// </summary>
    private string Tree(params string[] segments)
    {
        var root = _temp.CreateDirectory(segments);

        for (var folder = 0; folder < 4; folder++)
        {
            for (var i = 0; i < FilesPerFolder; i++)
            {
                _temp.CreateFile(16, [.. segments, $"d{folder}", $"f{i}.bin"]);
            }
        }

        return root;
    }

    /// <summary>
    /// Cancels at the first sign of work inside a step, rather than at a report the executor makes
    /// between steps, so the removal is stopped with files already gone.
    /// </summary>
    private static CallbackProgress<double> CancelOnFirstWork(CancellationTokenSource cts) =>
        new(fraction =>
        {
            if (fraction > 0)
            {
                cts.Cancel();
            }
        });

    private static CleanupPlan Plan(IReadOnlyList<CleanupStep> steps, params ProtectedPath[] protectedPaths) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
        ProtectedPaths = protectedPaths,
    };
}
