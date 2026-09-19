using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6's negative, and the question it has to answer before it raises an alarm: <em>did this run
/// do it?</em>
///
/// <para>A plan is built when the user presses Preview and carried out when they press Clean, and
/// <see cref="ProtectedPath.PresenceBefore"/> is a claim about the first of those instants. The
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

    private static ProtectedPath Protect(string path) => new(path, "It must survive.", PresenceBefore: PathPresence.Present);

    private static ProtectedPath ProtectRefused(string path) =>
        new(path, "It must survive.", PresenceBefore: PathPresence.Refused);

    private static VerificationOutcome OutcomeFor(CleanupPlan plan, string path, RunReach? reach = null) =>
        PlanVerifier.Verify(plan, reach).Checks.Single(c => c.Subject == path).Outcome;

    /// <summary>
    /// A volume root this machine has not mounted, chosen rather than written down. A literal drive
    /// letter makes the test's answer depend on what happens to be mapped here, which is what G8's
    /// "test through the fakes, never against the real machine" refuses.
    /// </summary>
    private static string UnmountedVolumeRoot() =>
        Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(root => !LongPath.DirectoryExists(root))
        ?? throw new InvalidOperationException("Every drive letter from D: to Z: is in use here.");

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
            new ProtectedPath(absent, "It must survive.", PresenceBefore: PathPresence.Absent));

        Assert.Equal(VerificationOutcome.NotPresentBefore, OutcomeFor(plan, absent));
        Assert.True(PlanVerifier.Verify(plan).Passed);
    }

    /// <summary>
    /// A survivor Windows would not describe when the plan was made, and which is gone afterwards.
    /// Recorded as absent, it read "nothing to preserve" and the check passed over whatever had
    /// taken it, which is the one shape in which §5.6 could not catch an over-broad rule.
    /// </summary>
    [Fact]
    public void ASurvivorWindowsWouldNotDescribeBeforeTheRunThatIsGoneAfterItIsAFailure()
    {
        var target = _temp.CreateDirectory("project", "obj");
        var vanished = _temp.CreateDirectory("project", "bin");
        var plan = Plan([new DeleteDirectoryStep(target, "Output")], ProtectRefused(vanished));

        Directory.Delete(vanished);

        var verification = PlanVerifier.Verify(plan);
        var check = Assert.Single(verification.Checks);

        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("would not describe it before the clean", check.Detail, StringComparison.Ordinal);
        Assert.False(verification.Passed);
    }

    /// <summary>
    /// The same survivor, described after the run. Nothing saw it before, and it is there now, so it
    /// survived: the claim a protected path makes is about the end of the run.
    /// </summary>
    [Fact]
    public void ASurvivorWindowsWouldNotDescribeBeforeTheRunThatIsThereAfterItSurvived()
    {
        var kept = _temp.CreateDirectory("project", "bin");
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")], ProtectRefused(kept));

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, kept));
        Assert.True(PlanVerifier.Verify(plan).Passed);
    }

    /// <summary>
    /// A survivor Windows will not describe after the run, whatever it said before. Reading that as
    /// missing raised an alarm on every run for a cache behind a link Windows declines to follow, and
    /// reading it as a survivor claims what nobody saw. It is a check that could not be made.
    /// </summary>
    [Theory]
    [InlineData(PathPresence.Present, "it was there before the clean")]
    [InlineData(PathPresence.Refused, "before the clean or after it")]
    public void ASurvivorWindowsWillNotDescribeAfterTheRunIsUnverified(PathPresence before, string detail)
    {
        var survivor = _temp.CreateDirectory("project", "bin");
        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")],
            new ProtectedPath(survivor, "It must survive.", before));

        using var denied = DeniedDirectory.WithUnreadableAttributes(survivor);

        var verification = PlanVerifier.Verify(plan);
        var check = Assert.Single(verification.Checks);

        Assert.Equal(VerificationOutcome.Unverified, check.Outcome);
        Assert.Contains(detail, check.Detail, StringComparison.Ordinal);
        Assert.Equal(survivor, Assert.Single(verification.Unverified).Subject);
        Assert.Empty(verification.Failures);
        Assert.Empty(verification.RemovedFromOutside);
        Assert.False(verification.Passed);
    }

    /// <summary>
    /// A refusal afterwards hides nothing the removal itself recorded. Deguffer's own deletion went
    /// inside the survivor, which is the alarm whatever Windows will now say about it.
    /// </summary>
    [Fact]
    public void ASurvivorARemovalWentIntoIsEnteredEvenWhereWindowsWillNotDescribeItAfterwards()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        var working = _temp.CreateDirectory("scratch", "live", "work");
        var plan = Plan([new ClearDirectoryStep(scratch, "Scratch files")], Protect(live));

        var residue = new RunResidue();
        residue.Record(scratch, [working, live]);

        using var denied = DeniedDirectory.WithUnreadableAttributes(live);

        var verification = PlanVerifier.Verify(plan, runReach: null, residue);

        Assert.Equal(VerificationOutcome.Entered, Assert.Single(verification.Checks).Outcome);
        Assert.Equal(live, Assert.Single(verification.Failures).Subject);
    }

    /// <summary>
    /// The outside reading rests on the folder that held a missing path being gone. A folder Windows
    /// will not describe is no evidence of that, so the missing path stays the alarm.
    /// </summary>
    [Fact]
    public void AMissingPathWhoseFolderWindowsWillNotDescribeIsNotReadAsRemovedFromOutside()
    {
        var folder = _temp.CreateDirectory("checkout", "project");
        var missing = Path.Combine(folder, "obj");
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")], Protect(missing));

        using var denied = DeniedDirectory.WithUnreadableAttributes(folder);

        // The shape under test, asserted rather than assumed: the path answers as absent while the
        // folder holding it will not answer at all.
        Assert.Equal(PathPresence.Absent, LongPath.ProbeEntry(missing));
        Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(folder));

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, missing));
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
        Assert.Contains(verification.Failures, c => c.Subject == vanished);
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
        Assert.Contains(verification.RemovedFromOutside, c => c.Subject == vanished);

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
        var root = UnmountedVolumeRoot();
        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("obj"), "Output")], Protect(root));

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, root));
    }

    /// <summary>
    /// The plainest over-reach there is: execution goes one directory higher than the plan named.
    /// The deselected sibling and the folder holding it are both gone, and neither is under a
    /// target — every condition for "something else did it", about a directory this run was working
    /// inside. The folder holding a target is the half of the evidence that refuses it.
    /// </summary>
    [Fact]
    public void APathWhoseFolderThisRunWasDeletingInsideIsAFailure()
    {
        var project = _temp.CreateDirectory("project");
        var target = _temp.CreateDirectory("project", "obj2");
        var deselected = _temp.CreateDirectory("project", "obj");

        // The shape CleanupPlan.NarrowedTo produces: the sibling the user unticked is protected, and
        // the one they left ticked is the step.
        var plan = Plan([new DeleteDirectoryStep(target, "Output")], Protect(deselected));

        Directory.Delete(project, recursive: true);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, deselected));
    }

    /// <summary>
    /// A run is many plans, and a folder another provider deleted is not a folder a stranger
    /// deleted. Verified against this plan alone the disappearance is indistinguishable from an
    /// outside removal, which is Deguffer's own hand suppressing a §5.6 alarm.
    /// </summary>
    [Fact]
    public void APathWhoseFolderAnotherPlanInTheRunWasDeletingInsideIsAFailure()
    {
        var project = _temp.CreateDirectory("project");
        var otherPlansTarget = _temp.CreateDirectory("project", "node_modules");
        var protectedPath = _temp.CreateDirectory("project", "obj");

        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")], Protect(protectedPath));
        var reach = new RunReach([.. plan.TargetedPaths, otherPlansTarget], ProbedPaths: [], Unbounded: false);

        Directory.Delete(project, recursive: true);

        // Its own plan's reach cannot see the other provider's target, so on its own it reads as an
        // outside removal. The run's reach is what makes the answer honest.
        Assert.Equal(VerificationOutcome.RemovedFromOutside, OutcomeFor(plan, protectedPath));
        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, protectedPath, reach));
    }

    /// <summary>
    /// §5.1 leaves an eviction command deciding what it removes, and a run holds many plans, so one
    /// provider's command makes the whole run's reach unbounded — including for the delete-only
    /// providers beside it, which is where the suppressed alarm would otherwise land.
    /// </summary>
    [Fact]
    public void ACommandStepInAnotherPlanLeavesThisOneAnsweringForTheRunToo()
    {
        var tree = _temp.CreateDirectory("cache");
        var protectedPath = _temp.CreateDirectory("cache", "config");

        var plan = Plan([new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")], Protect(protectedPath));
        var reach = new RunReach(plan.TargetedPaths, ProbedPaths: [], Unbounded: true);

        Directory.Delete(tree, recursive: true);

        Assert.Equal(VerificationOutcome.RemovedFromOutside, OutcomeFor(plan, protectedPath));
        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, protectedPath, reach));
    }

    /// <summary>
    /// §5.1 leaves a command deciding what it removes, and that is a reason to answer for whatever
    /// the run empties, never a reason to excuse it. A folder the plan protected and never sent the
    /// tool into is the over-reach this check exists for, whatever else the run holds.
    /// </summary>
    [Fact]
    public void ACommandStepDoesNotExcuseEmptyingAFolderItNeverDeclared()
    {
        var cache = _temp.CreateDirectory("tool", "cache");
        var config = _temp.CreateDirectory("tool", "config");
        var settings = _temp.CreateFile(8, "tool", "config", "settings.json");

        var plan = Plan([Evict(cache)], ProtectHolding(config));

        File.Delete(settings);

        Assert.Equal(VerificationOutcome.Emptied, OutcomeFor(plan, config));
        Assert.False(PlanVerifier.Verify(plan).Passed);
    }

    /// <summary>
    /// The case the exemption is for. A tool's cache and the root holding it are both protected, and
    /// the command is sent to clear exactly that cache, so both end the run holding nothing. Each of
    /// them holds the path the tool was sent to, which is the plan's own declared work.
    ///
    /// <para>The cache and the root are separate assertions because they exercise different halves
    /// of the containment: the cache is the probed path itself, and the root sits above it. The
    /// negative, a folder beside the cache that the tool was never sent to, is
    /// <see cref="ACommandStepDoesNotExcuseEmptyingAFolderItNeverDeclared"/>.</para>
    /// </summary>
    [Fact]
    public void AFolderHoldingACommandsDeclaredReachMayEndTheRunEmpty()
    {
        var root = _temp.CreateDirectory("tool");
        var cache = _temp.CreateDirectory("tool", "cache");
        var entry = _temp.CreateFile(8, "tool", "cache", "entry.bin");

        var plan = Plan([Evict(cache)], ProtectHolding(root), ProtectHolding(cache));

        // Emptied in place, which is the shape a tool's own clear commonly leaves.
        File.Delete(entry);

        // The shape the exemption has to excuse, asserted rather than assumed: nothing is left
        // anywhere under the root.
        Assert.False(DirectoryContent.IsPresent(root));

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, root));
        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, cache));
    }

    /// <summary>
    /// A run is many plans, so the declared reach that excuses an emptied folder is the whole run's,
    /// as a target is. It excuses the folders above the path the other plan's tool was sent to, and
    /// nothing beside it.
    /// </summary>
    [Fact]
    public void AnotherPlansDeclaredReachExcusesOnlyTheFoldersAboveIt()
    {
        var shared = _temp.CreateDirectory("shared");
        var theirCache = _temp.CreateDirectory("shared", "cache");
        _temp.CreateFile(8, "shared", "cache", "entry.bin");
        var config = _temp.CreateDirectory("config");
        var settings = _temp.CreateFile(8, "config", "settings.json");

        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("elsewhere", "obj"), "Output")],
            ProtectHolding(shared),
            ProtectHolding(config));
        var reach = RunReach.Of([plan, Plan([Evict(theirCache)])]);

        Directory.Delete(theirCache, recursive: true);
        File.Delete(settings);

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, shared, reach));
        Assert.Equal(VerificationOutcome.Emptied, OutcomeFor(plan, config, reach));
    }

    /// <summary>
    /// In a run holding a tool's own command, content is asked for where it lives. A tool can take
    /// every file and leave the folders that held them, which still takes everything worth
    /// protecting, and a top-level question would see a folder holding a folder and call it a
    /// survivor.
    /// </summary>
    [Fact]
    public void AFolderAToolLeftHoldingOnlyEmptyFoldersWasEmptied()
    {
        var kept = _temp.CreateDirectory("project", "bin");
        var assembly = _temp.CreateFile(8, "project", "bin", "Debug", "net10.0", "app.dll");
        var plan = Plan([Evict(_temp.CreateDirectory("tool", "cache"))], ProtectHolding(kept));

        File.Delete(assembly);

        Assert.True(Directory.Exists(Path.GetDirectoryName(assembly)));
        Assert.Equal(VerificationOutcome.Emptied, OutcomeFor(plan, kept));
    }

    /// <summary>
    /// The same shape in a run with no tool's command. MSBuild's Clean, run while the preview sat on
    /// screen, leaves exactly this beside the <c>obj</c> the run removes, and Deguffer never touched
    /// it, so the question here is whether anything at all is left. A folder with nothing in it is
    /// still an alarm. The same shape left by Deguffer's own removal, when Windows refuses a folder
    /// inside it, is answered by what that removal recorded instead: see
    /// <see cref="AProtectedFolderARemovalWentIntoWasEntered"/>.
    /// </summary>
    [Fact]
    public void WithoutACommandAFolderEmptiedOnlyOfItsFilesIsNotReadAsEmptied()
    {
        var kept = _temp.CreateDirectory("project", "bin");
        var output = _temp.CreateDirectory("project", "bin", "Debug");
        var assembly = _temp.CreateFile(8, "project", "bin", "Debug", "net10.0", "app.dll");
        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")],
            ProtectHolding(kept));

        File.Delete(assembly);

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, kept));

        Directory.Delete(output, recursive: true);

        Assert.Equal(VerificationOutcome.Emptied, OutcomeFor(plan, kept));
    }

    /// <summary>
    /// A folder still holding a file far below, in a run where the question looks that far: it
    /// survived.
    /// </summary>
    [Fact]
    public void AFolderStillHoldingAFileFarBelowSurvived()
    {
        var kept = _temp.CreateDirectory("project", "bin");
        _temp.CreateFile(8, "project", "bin", "Debug", "net10.0", "app.dll");
        var plan = Plan([Evict(_temp.CreateDirectory("tool", "cache"))], ProtectHolding(kept));

        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, kept));
    }

    /// <summary>
    /// The defect in issue #118. A clear that went into a folder it should have spared met a folder a
    /// program was working in, which Windows would not remove. It took every file and left a chain of
    /// empty folders, so the protected folder still held a folder and read as a survivor. What the
    /// removal left standing inside it is the evidence, whatever the folder still holds.
    /// </summary>
    [Fact]
    public void AProtectedFolderARemovalWentIntoWasEntered()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        var working = _temp.CreateDirectory("scratch", "live", "session", "work");
        var plan = Plan([new ClearDirectoryStep(scratch, "Scratch files")], ProtectHolding(live));

        // The shape the record has to see through, asserted rather than assumed: without it, a survivor.
        Assert.Equal(VerificationOutcome.Survived, OutcomeFor(plan, live));

        var residue = new RunResidue();
        residue.Record(scratch, [working, Path.GetDirectoryName(working)!, live]);

        var verification = PlanVerifier.Verify(plan, runReach: null, residue);

        Assert.Equal(VerificationOutcome.Entered, Assert.Single(verification.Checks).Outcome);
        Assert.Equal(live, Assert.Single(verification.Failures).Subject);
        Assert.False(verification.Passed);
    }

    /// <summary>
    /// The partial over-reach no question about content can see: one file the removal was refused,
    /// still sitting in the protected folder. The folder is exactly as present as a survivor.
    /// </summary>
    [Fact]
    public void AProtectedFolderStillHoldingAFileARemovalWasRefusedWasEntered()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        _temp.CreateFile(8, "scratch", "live", "session.lock");
        var plan = Plan([new ClearDirectoryStep(scratch, "Scratch files")], ProtectHolding(live));

        var residue = new RunResidue();
        residue.Record(scratch, [live]);

        Assert.Equal(VerificationOutcome.Entered, PlanVerifier.Verify(plan, runReach: null, residue).Checks.Single().Outcome);
    }

    /// <summary>
    /// The negative. A removal's own root holds what it left behind by right, a folder above the root
    /// holds its target, and a protected folder beside the one it went into was never entered at all.
    /// None of them is accused.
    /// </summary>
    [Fact]
    public void WhatARemovalLeftStandingAccusesOnlyTheFolderItWentInto()
    {
        var tool = _temp.CreateDirectory("tool");
        var cache = _temp.CreateDirectory("tool", "cache");
        var entry = _temp.CreateDirectory("tool", "cache", "entry");
        var beside = _temp.CreateDirectory("tool", "cache", "beside");
        _temp.CreateFile(8, "tool", "cache", "beside", "keep.bin");

        var plan = Plan(
            [new DeleteDirectoryStep(cache, "A cache")],
            ProtectHolding(tool),
            ProtectHolding(cache),
            ProtectHolding(beside),
            ProtectHolding(entry));

        var residue = new RunResidue();
        residue.Record(cache, [entry, cache]);

        var outcomes = PlanVerifier.Verify(plan, runReach: null, residue).Checks.ToDictionary(c => c.Subject, c => c.Outcome);

        Assert.Equal(VerificationOutcome.Survived, outcomes[tool]);
        Assert.Equal(VerificationOutcome.Survived, outcomes[cache]);
        Assert.Equal(VerificationOutcome.Survived, outcomes[beside]);
        Assert.Equal(VerificationOutcome.Entered, outcomes[entry]);
    }

    /// <summary>
    /// A target inside a protected folder answers for what its own removal left, and for nothing a
    /// removal from above left beside it. Excusing the whole folder because it holds a target would
    /// pass over exactly that second removal.
    /// </summary>
    [Fact]
    public void ATargetInsideAProtectedFolderExcusesOnlyWhatItsOwnRemovalLeft()
    {
        var outer = _temp.CreateDirectory("outer");
        var project = _temp.CreateDirectory("outer", "project");
        var obj = _temp.CreateDirectory("outer", "project", "obj");
        var inObj = _temp.CreateDirectory("outer", "project", "obj", "held");
        var inBin = _temp.CreateDirectory("outer", "project", "bin", "held");

        var plan = Plan(
            [new DeleteDirectoryStep(obj, "Output"), new ClearDirectoryStep(outer, "Everything")],
            ProtectHolding(project));

        var fromTheTarget = new RunResidue();
        fromTheTarget.Record(obj, [inObj]);

        var fromAbove = new RunResidue();
        fromAbove.Record(outer, [inBin]);

        Assert.Equal(VerificationOutcome.Survived, PlanVerifier.Verify(plan, runReach: null, fromTheTarget).Checks.Single().Outcome);
        Assert.Equal(VerificationOutcome.Entered, PlanVerifier.Verify(plan, runReach: null, fromAbove).Checks.Single().Outcome);
    }

    /// <summary>A protected directory recorded as holding something, as a provider's capture records it.</summary>
    private static ProtectedPath ProtectHolding(string path) =>
        new(path, "It must survive.", PresenceBefore: PathPresence.Present, HeldContentBefore: true);

    /// <summary>A tool's own eviction command, sent to clear <paramref name="cache"/>.</summary>
    private static RunCommandStep Evict(string cache) =>
        new("tool.exe", "cache clean", "Clear the cache using the tool's own command") { MeasuredPaths = [cache] };

    /// <summary>
    /// A protected path spelled with a trailing separator, which a provider's own Path.Combine can
    /// produce. Without the normalisation, Path.GetDirectoryName hands back the path itself — which
    /// is missing by definition here — and every such check would read as an outside removal.
    /// </summary>
    [Fact]
    public void ATrailingSeparatorDoesNotMakeAPathItsOwnFolder()
    {
        _temp.CreateDirectory("project");
        var vanished = _temp.CreateDirectory("project", "bin");
        var plan = Plan(
            [new DeleteDirectoryStep(_temp.CreateDirectory("project", "obj"), "Output")],
            Protect(vanished + Path.DirectorySeparatorChar));

        Directory.Delete(vanished);

        Assert.Equal(VerificationOutcome.Failed, OutcomeFor(plan, vanished + Path.DirectorySeparatorChar));
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

    /// <summary>
    /// A result holding both kinds accounts for both. Naming the failures alone would say "1 of 3"
    /// about a run where two paths went unverified, and a §5.6 report that states less than it
    /// established is the overstatement's mirror image.
    /// </summary>
    /// <summary>
    /// A path nobody could check is counted in the denominator and named in the sentence. Leaving it
    /// out would say "all 1 survived" about a run that checked one of two.
    /// </summary>
    [Fact]
    public void TheSummaryCountsWhatCouldNotBeChecked()
    {
        var result = new VerificationResult
        {
            Checks = [Check(VerificationOutcome.Survived), Check(VerificationOutcome.Unverified)],
        };

        Assert.Equal("1 of 2 protected item(s) could not be checked.", result.Summary);
    }

    /// <summary>Every kind a run can have to answer for, each counted once, the alarm first.</summary>
    [Fact]
    public void TheSummaryCountsEveryKindWhenARunHasAll()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Survived),
                Check(VerificationOutcome.Unverified),
                Check(VerificationOutcome.Failed),
                Check(VerificationOutcome.RemovedFromOutside),
            ],
        };

        Assert.Equal(
            "1 of 4 protected item(s) did not survive, 1 more were removed from outside this run, and "
            + "1 more could not be checked.",
            result.Summary);
    }

    [Fact]
    public void TheSummaryCountsBothKindsWhenARunHasBoth()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Survived),
                Check(VerificationOutcome.Failed),
                Check(VerificationOutcome.RemovedFromOutside),
            ],
        };

        Assert.Equal(
            "1 of 3 protected item(s) did not survive, and 1 more were removed from outside this run.",
            result.Summary);
    }

    private static VerificationCheck Check(VerificationOutcome outcome) =>
        new($@"C:\Users\testuser\src\{outcome}", "It must survive.", outcome, "Whatever was found.");
}
