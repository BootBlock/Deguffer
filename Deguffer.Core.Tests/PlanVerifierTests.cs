using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6's negative, and the question it has to answer before it raises an alarm: <em>did this run
/// do it?</em>
///
/// <para>A plan is built when the user presses Preview and carried out when they press Clean, and
/// <see cref="ProtectedPath.ExistedBefore"/> is a claim about the first of those instants. The
/// machine is free to change in between, and on a developer's disk it does — a source checkout
/// removed while the preview sat on screen took a whole tree of protected paths with it, and every
/// one of them was reported as a rule that had reached too far.</para>
///
/// <para>The evidence that separates the two is the folder rather than the path.
/// <see cref="DirectoryRemover"/> stays inside the tree under a step's path and never touches that
/// tree's own parent, so Deguffer cannot have removed the folder holding a protected path unless
/// the plan named that folder or something above it. A missing path whose folder is missing too was
/// taken by something else; a missing path in a folder still standing is what an over-broad rule
/// looks like.</para>
/// </summary>
public sealed class PlanVerifierTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CleanupPlan Plan(
        IReadOnlyList<CleanupStep> steps,
        params ProtectedPath[] protectedPaths) => new()
        {
            ProviderId = "test",
            ProviderName = "Test provider",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "It comes back.",
            Steps = steps,
            ProtectedPaths = protectedPaths,
        };

    private static ProtectedPath Protect(string path) => new(path, "It must survive.", ExistedBefore: true);

    private static VerificationOutcome OutcomeFor(CleanupPlan plan, string path) =>
        PlanVerifier.Verify(plan).Checks.Single(c => c.Path == path).Outcome;

    [Fact]
    public void APathStillStandingSurvived()
    {
        var kept = _temp.CreateDirectory("project", "bin");
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")], Protect(kept));

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, kept));
        Assert.True(PlanVerifier.Verify(plan).Passed);
    }

    /// <summary>
    /// A path that was never there cannot be evidence of survival, and the report says so rather
    /// than quietly counting it as one.
    /// </summary>
    [Fact]
    public void APathThatWasNeverThereIsRecordedAsSuch()
    {
        var absent = Path.Combine(_temp.Path, "project", "bin");
        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")],
            new ProtectedPath(absent, "It must survive.", ExistedBefore: false));

        Assert.Equal(VerificationOutcome.NotPresentBefore, OutcomeFor(plan, absent));
        Assert.True(PlanVerifier.Verify(plan).Passed);
    }

    /// <summary>
    /// The alarm this whole mechanism exists for. The folder is still there and the thing inside it
    /// is not, which is what a rule reaching one directory too far leaves behind — including one
    /// that escaped a tree it was meant to stay inside, because the far side's own root survives.
    /// </summary>
    [Fact]
    public void APathTakenFromAFolderThatIsStillThereIsAFailure()
    {
        var target = _temp.CreateDirectory("project", "obj");
        var vanished = _temp.CreateDirectory("project", "bin");
        var plan = Plan([new DeleteDirectoryStep(target, "Output")], Protect(vanished));

        Directory.Delete(vanished);

        var verification = PlanVerifier.Verify(plan);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished));
        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path == vanished);
        Assert.Empty(verification.RemovedFromOutside);
    }

    /// <summary>
    /// The reported defect: a git worktree removed while the preview sat on screen. The protected
    /// path and the folder holding it both went, and no step named either — so this run did not do
    /// it, and saying it did sends the user to report a fault that is not there.
    /// </summary>
    [Fact]
    public void APathWhoseFolderWentWithItWasTakenFromOutsideTheRun()
    {
        var checkout = _temp.CreateDirectory("checkout");
        var project = _temp.CreateDirectory("checkout", "project");
        var vanished = _temp.CreateDirectory("checkout", "project", "obj");
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")], Protect(vanished));

        Directory.Delete(checkout, recursive: true);

        var verification = PlanVerifier.Verify(plan);

        Assert.Equal(VerificationOutcome.RemovedFromOutside, OutcomeFor(plan, vanished));
        Assert.Empty(verification.Failures);
        Assert.Contains(verification.RemovedFromOutside, c => c.Path == vanished);

        // Not a pass either. Nobody verified that path, and the run's figures describe a machine
        // that moved underneath them.
        Assert.False(verification.Passed);
        Assert.False(Directory.Exists(project));
    }

    /// <summary>
    /// The folder went, and this plan is why: it named the folder. That is over-reach of the most
    /// direct kind — a step one directory too high — and the missing-folder evidence must not
    /// excuse it.
    /// </summary>
    [Fact]
    public void APathInsideAFolderThisRunTargetedIsAFailure()
    {
        var project = _temp.CreateDirectory("project");
        var vanished = _temp.CreateDirectory("project", "obj");
        var plan = Plan([new DeleteDirectoryStep(project, "Output")], Protect(vanished));

        Directory.Delete(project, recursive: true);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished));
    }

    /// <summary>
    /// The path itself was a step's own target, so the plan destroyed the thing it also promised
    /// would survive. Its folder is gone as well, and that changes nothing.
    /// </summary>
    [Fact]
    public void APathThisRunTargetedOutrightIsAFailure()
    {
        var project = _temp.CreateDirectory("project");
        var vanished = _temp.CreateDirectory("project", "obj");
        var plan = Plan([new DeleteDirectoryStep(vanished, "Output")], Protect(vanished));

        Directory.Delete(project, recursive: true);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished));
    }

    /// <summary>
    /// §5.1 leaves a tool's own eviction command deciding what it removes, so a plan holding one has
    /// no bounded reach to measure a disappearance against. Everything stays this run's to answer
    /// for — which is the case the Go module cache's installed binaries stand for.
    /// </summary>
    [Fact]
    public void ACommandStepLeavesEveryDisappearanceThisRunsToAnswerFor()
    {
        var tree = _temp.CreateDirectory("gopath", "bin");
        var vanished = _temp.CreateDirectory("gopath", "bin", "tools");
        var plan = Plan(
            [new RunCommandStep("go.exe", "clean -modcache", "Clear the module cache")],
            Protect(vanished));

        Directory.Delete(tree, recursive: true);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished));
    }

    /// <summary>
    /// §6.3 lets a step carry the extended-length prefix where a protected path does not. Compared
    /// as they arrive, the containment test would answer no about a path the run deleted outright,
    /// and the over-reach above would be excused as an outside removal.
    /// </summary>
    [Fact]
    public void TheExtendedLengthPrefixOnAStepDoesNotHideOverReach()
    {
        var project = _temp.CreateDirectory("project");
        var vanished = _temp.CreateDirectory("project", "obj");
        var plan = Plan([new DeleteDirectoryStep(LongPath.Extended(project), "Output")], Protect(vanished));

        Directory.Delete(project, recursive: true);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished));
    }

    /// <summary>
    /// A volume root has no folder above it, so the evidence an outside removal rests on cannot
    /// exist. Every branch that cannot establish one answers with the alarming reading, because a
    /// false alarm costs a look at the folder and a missed one costs the folder.
    /// </summary>
    [Fact]
    public void APathWithNoFolderAboveItStaysAFailure()
    {
        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("obj"), "Output")],
            Protect(@"Q:\"));

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, @"Q:\"));
    }

    /// <summary>
    /// The summary is the sentence a test failure prints, so it has to name which of the three
    /// things happened rather than collapsing two of them into "did not survive".
    /// </summary>
    [Fact]
    public void TheSummaryTellsAnOutsideRemovalApartFromAFailure()
    {
        var checkout = _temp.CreateDirectory("checkout");
        var vanished = _temp.CreateDirectory("checkout", "obj");
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")], Protect(vanished));

        Directory.Delete(checkout, recursive: true);

        var summary = PlanVerifier.Verify(plan).Summary;

        Assert.Contains("removed from outside this run", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("did not survive", summary, StringComparison.Ordinal);
    }
}
