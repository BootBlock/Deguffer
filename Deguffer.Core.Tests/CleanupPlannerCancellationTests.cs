using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A run the user cancels between plans. The results of the plans that ran are kept, the plans never
/// started are verified, and the plans run only to prove what the run leaves standing still run
/// (§5.6). §7's confirmations are all asked for before anything is deleted.
/// </summary>
public sealed class CleanupPlannerCancellationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The first plan runs to its end and the clean is cancelled behind it. The second never starts,
    /// and is verified: its protected folder was inside what the first plan removed, which only its
    /// own check could report. The proof-only plan still runs, last.
    /// </summary>
    [Fact]
    public async Task ACancelledRunKeepsWhatRanAndVerifiesEveryPlan()
    {
        var removed = _temp.CreateDirectory("tool", "cache");
        var promised = _temp.CreateDirectory("tool", "cache", "shared");
        var untouched = _temp.CreateDirectory("other", "cache");
        var kept = _temp.CreateDirectory("kept", "item");

        using var cts = new CancellationTokenSource();

        var first = new FakeCleanupProvider("first")
        {
            Steps = [Step(removed, 2_000)],
            AfterCleaning = cts.Cancel,
        };

        var second = new FakeCleanupProvider("second")
        {
            Steps = [Step(untouched, 1_000)],
            ProtectedPaths = [new ProtectedPath(promised, "Shared with the first tool.", PathPresence.Present)],
        };

        var proof = new FakeCleanupProvider("proof")
        {
            ProtectedPaths =
            [
                new ProtectedPath(kept, "On the keep list.", PathPresence.Present, Withheld: Withholding.OnKeepList),
            ],
        };

        var planner = new CleanupPlanner([first, second, proof]);
        var results = await planner.ExecuteAsync(await planner.PlanAllAsync(), ct: cts.Token);

        Assert.Equal(["first", "second", "proof"], results.Select(r => r.ProviderId));

        var ran = results[0];
        Assert.False(ran.Interrupted);
        Assert.Single(ran.Steps);

        var unstarted = results[1];
        Assert.True(unstarted.Interrupted);
        Assert.Empty(unstarted.Steps);
        Assert.Empty(second.Executed);
        Assert.True(Directory.Exists(untouched), "a plan the cancelled run never started was carried out");
        Assert.Equal(VerificationOutcome.Failed, Assert.Single(unstarted.Verification!.Checks).Outcome);

        Assert.Single(proof.Executed);
        Assert.False(results[2].Interrupted);
        Assert.Equal(VerificationOutcome.Survived, Assert.Single(results[2].Verification!.Checks).Outcome);
        Assert.True(Directory.Exists(kept));
    }

    /// <summary>
    /// A run cancelled before it starts deletes nothing, verifies every plan, and still proves what
    /// the unticked rows leave standing.
    /// </summary>
    [Fact]
    public async Task ARunCancelledBeforeItStartsDeletesNothingAndVerifiesEveryPlan()
    {
        var cache = _temp.CreateDirectory("tool", "cache");
        var root = Path.GetDirectoryName(cache)!;
        var kept = _temp.CreateDirectory("kept", "item");

        var cleaning = new FakeCleanupProvider("cleaning")
        {
            Steps = [Step(cache, 1_000)],
            ProtectedPaths = [new ProtectedPath(root, "The tool's own folder.", PathPresence.Present)],
        };

        var proof = new FakeCleanupProvider("proof")
        {
            ProtectedPaths =
            [
                new ProtectedPath(kept, "On the keep list.", PathPresence.Present, Withheld: Withholding.OnKeepList),
            ],
        };

        var planner = new CleanupPlanner([cleaning, proof]);
        var findings = await planner.PlanAllAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var results = await planner.ExecuteAsync(findings, ct: cts.Token);

        Assert.Empty(cleaning.Executed);
        Assert.True(Directory.Exists(cache));
        Assert.True(results[0].Interrupted);
        Assert.Equal(VerificationOutcome.Survived, Assert.Single(results[0].Verification!.Checks).Outcome);
        Assert.Single(proof.Executed);
        Assert.True(results[1].Verification!.Passed);
    }

    /// <summary>
    /// A later plan whose confirmation is missing refuses the run before the first deletion. Asked in
    /// the loop instead, it threw after an earlier plan had deleted, and that plan's result and its
    /// §5.6 verdict went with the exception.
    /// </summary>
    [Fact]
    public async Task AMissingConfirmationRefusesTheRunBeforeAnythingIsDeleted()
    {
        var cache = _temp.CreateDirectory("cache");
        var sdk = _temp.CreateDirectory("sdk");

        var first = new FakeCleanupProvider("cache") { Steps = [Step(cache, 2_000)] };
        var costly = new FakeCleanupProvider("sdk", SafetyTier.RegenerableWithCost) { Steps = [Step(sdk, 1_000)] };

        var planner = new CleanupPlanner([first, costly]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(() => planner.ExecuteAsync(findings));

        Assert.Empty(first.Executed);
        Assert.True(Directory.Exists(cache));
        Assert.True(Directory.Exists(sdk));
    }

    private static DeleteDirectoryStep Step(string path, long bytes) =>
        new(path, Path.GetFileName(path)) { Estimated = new ScanSize(bytes, bytes) };
}
