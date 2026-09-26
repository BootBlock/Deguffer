using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The keep list applied to a plan, driven through <see cref="PlaywrightBrowsersProvider"/> because
/// it names its items and deletes real directories, rather than because the rules are Playwright's.
///
/// <para>Every test that runs a plan asserts the negative (§5.6) as well as the positive: what was
/// kept or never recognised is still there, and so is the registry beside the builds.</para>
///
/// <para>What these do not reach is the tick itself. Whether a kept item's checkbox can be ticked is
/// <see cref="Deguffer.Core.Choosing.StepChoice"/>'s rule, tested beside it; the Core half is asserted
/// here — a kept item is not a step, so no selection of steps can run it.</para>
/// </summary>
public sealed class KeepListPlanTests : IDisposable
{
    private const string Chromium = "chromium-1228";
    private const string Firefox = "firefox-1532";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public KeepListPlanTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string DefaultRoot => Path.Combine(_environment.LocalAppData, "ms-playwright");

    private PlaywrightBrowsersProvider Provider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>A browser cache holding the given builds, each with a payload, and Playwright's registry.</summary>
    private static string CreateBuilds(string root, params string[] builds)
    {
        foreach (var build in builds)
        {
            Directory.CreateDirectory(Path.Combine(root, build));
            File.WriteAllBytes(Path.Combine(root, build, "payload.bin"), new byte[4096]);
        }

        Directory.CreateDirectory(Path.Combine(root, ".links"));
        File.WriteAllText(Path.Combine(root, ".links", "marker"), "project");

        return root;
    }

    private static IReadOnlySet<string> Keys(params string[] keys) =>
        new KeepList(keys.Select(key => new KeptItem("playwright", "Playwright browsers", new ItemIdentity(key, key))))
            .KeysFor("playwright");

    [Fact]
    public async Task AKeptBuildIsProtectedRatherThanTargetedAndSurvivesTheRunThatRemovesItsSibling()
    {
        var root = CreateBuilds(DefaultRoot, Chromium, Firefox);
        var kept = Path.Combine(root, Chromium);
        var removed = Path.Combine(root, Firefox);

        var provider = Provider();
        var plan = (await provider.PlanAsync()).WithKeepList(Keys(Chromium));

        Assert.Equal([removed], plan.TargetedPaths);
        Assert.True(plan.HoldsKeepListItems);
        Assert.Contains(plan.Notes, n => n.Message.Contains("on your keep list", StringComparison.Ordinal));

        var protection = Assert.Single(plan.ProtectedPaths, p => p.Path == kept);
        Assert.Equal(PathPresence.Present, protection.PresenceBefore);
        Assert.False(protection.HeldContentBefore);
        Assert.Equal(Withholding.OnKeepList, protection.Withheld);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(removed));
        Assert.True(File.Exists(Path.Combine(kept, "payload.bin")), "a kept build was deleted or emptied");
        Assert.True(File.Exists(Path.Combine(root, ".links", "marker")), "Playwright's registry did not survive");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Equal(VerificationOutcome.Survived, Assert.Single(result.Verification.Checks, c => c.Subject == kept).Outcome);
    }

    /// <summary>
    /// The case the keep list makes ordinary: every candidate in a row is kept, so the row has no step
    /// and is never ticked. Its promise is still owed evidence, and the run provides it — and that
    /// evidence is real, because a kept build that is gone is reported rather than passed over.
    /// </summary>
    [Fact]
    public async Task ARunInWhichEveryCandidateIsKeptStillVerifiesThatTheySurvived()
    {
        var root = CreateBuilds(DefaultRoot, Chromium, Firefox);
        var provider = Provider();
        var plan = (await provider.PlanAsync()).WithKeepList(Keys(Chromium, Firefox));

        Assert.True(plan.IsEmpty);
        Assert.True(plan.HasSomethingToProve);

        var planner = new CleanupPlanner([provider]);
        var proving = new Finding(provider, IsPresent: true, plan.ProofOnly());

        var result = Assert.Single(await planner.ExecuteAsync([proving]));

        Assert.Equal(
            new[] { Path.Combine(root, Chromium), Path.Combine(root, Firefox) }.Order(StringComparer.OrdinalIgnoreCase),
            result.Verification!.Checks.Select(c => c.Subject).Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(result.Verification.Passed, result.Verification.Summary);
        Assert.True(File.Exists(Path.Combine(root, Chromium, "payload.bin")), "a kept build was touched");
        Assert.True(File.Exists(Path.Combine(root, Firefox, "payload.bin")), "a kept build was touched");
        Assert.True(File.Exists(Path.Combine(root, ".links", "marker")), "Playwright's registry did not survive");

        Directory.Delete(Path.Combine(root, Chromium), recursive: true);

        var afterLoss = Assert.Single(await planner.ExecuteAsync([proving]));

        Assert.Equal(Path.Combine(root, Chromium), Assert.Single(afterLoss.Verification!.Failures).Subject);
    }

    /// <summary>
    /// The tool that owns a kept item goes on working on it. A kept directory emptied between the
    /// preview and the clean is ordinary, nothing Deguffer did caused it, and an alarm about it would
    /// cry wolf. That is the reason a kept item is protected on existence alone.
    /// </summary>
    [Fact]
    public async Task AKeptBuildEmptiedFromOutsideRaisesNoAlarm()
    {
        var root = CreateBuilds(DefaultRoot, Chromium, Firefox);
        var kept = Path.Combine(root, Chromium);

        var provider = Provider();
        var plan = (await provider.PlanAsync()).WithKeepList(Keys(Chromium));

        File.Delete(Path.Combine(kept, "payload.bin"));

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(VerificationOutcome.Survived, Assert.Single(result.Verification!.Checks, c => c.Subject == kept).Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(root, Firefox)));
        Assert.True(Directory.Exists(kept), "a kept build was deleted");
        Assert.True(File.Exists(Path.Combine(root, ".links", "marker")), "Playwright's registry did not survive");
    }

    /// <summary>
    /// What keying on identity rather than path buys: a cache the user moved still has its kept
    /// build kept, at the new path.
    /// </summary>
    [Fact]
    public async Task ACacheMovedToAnotherFolderKeepsItsKeepListEntries()
    {
        CreateBuilds(DefaultRoot, Chromium);
        var identity = Assert.IsAssignableFrom<DeleteStep>(Assert.Single((await Provider().PlanAsync()).Steps)).Identity;
        Assert.NotNull(identity);

        var keepList = new KeepList([new KeptItem("playwright", "Playwright browsers", identity)]);

        var moved = CreateBuilds(Path.Combine(_temp.Path, "relocated-browsers"), Chromium);
        Directory.Delete(DefaultRoot, recursive: true);
        _environment.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, moved);

        var plan = (await Provider().PlanAsync()).WithKeepList(keepList.KeysFor("playwright"));

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(
            plan.ProtectedPaths,
            p => p.Path == Path.Combine(moved, Chromium) && p.Withheld == Withholding.OnKeepList);
    }

    /// <summary>
    /// Nothing a path can say keeps an item: not the path, not the selection key, not the sentence
    /// describing it. A keep list keyed on any of them would stop matching the moment the item moved.
    /// </summary>
    [Fact]
    public async Task APathIsNeverAKeepListKey()
    {
        var root = CreateBuilds(DefaultRoot, Chromium);
        var plan = await Provider().PlanAsync();
        var step = Assert.IsAssignableFrom<DeleteStep>(Assert.Single(plan.Steps));

        var kept = plan.WithKeepList(Keys(step.Path, step.SelectionKey, step.Description, root));

        Assert.Same(plan, kept);
        Assert.Equal([Path.Combine(root, Chromium)], kept.TargetedPaths);
    }

    /// <summary>
    /// §5.2: the keep list only ever takes things out of a plan, so it cannot reach a child the
    /// provider does not recognise, whatever it names. That child stays unoffered and untouched.
    /// </summary>
    [Fact]
    public async Task TheKeepListCannotReachAChildTheProviderDoesNotRecognise()
    {
        var root = CreateBuilds(DefaultRoot, Chromium, "notabrowser-1");
        var stranger = Path.Combine(root, "notabrowser-1");

        var provider = Provider();
        var plan = (await provider.PlanAsync()).WithKeepList(Keys("notabrowser-1"));

        Assert.DoesNotContain(stranger, plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Withheld == Withholding.OnKeepList);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(stranger, "payload.bin")), "an unrecognised child was touched");
        Assert.False(Directory.Exists(Path.Combine(root, Chromium)));
        Assert.True(File.Exists(Path.Combine(root, ".links", "marker")), "Playwright's registry did not survive");
    }

    [Fact]
    public async Task ApplyingTheKeepListASecondTimeChangesNothing()
    {
        CreateBuilds(DefaultRoot, Chromium, Firefox);
        var once = (await Provider().PlanAsync()).WithKeepList(Keys(Chromium));

        Assert.Same(once, once.WithKeepList(Keys(Chromium)));
    }

    /// <summary>
    /// The polarity, asserted directly rather than trusted from the store's shape: at every tier,
    /// whatever the list holds, applying it only removes steps and only adds protections. A shell that
    /// ticked every step it was ever shown still cannot run a kept one, and a step with no identity
    /// cannot be kept by anything its path says.
    /// </summary>
    [Fact]
    public void NothingOnTheKeepListCanPutAnythingIntoARunAtAnyTier()
    {
        const string KeptPath = @"C:\Users\testuser\AppData\Local\example\kept";
        const string OfferedPath = @"C:\Users\testuser\AppData\Local\example\offered";
        const string AnonymousPath = @"C:\Users\testuser\AppData\Local\example\anonymous";

        foreach (var tier in Enum.GetValues<SafetyTier>())
        {
            var plan = new CleanupPlan
            {
                ProviderId = "example",
                ProviderName = "Example",
                Tier = tier,
                WhatHappensOnNextUse = "Nothing.",
                Steps =
                [
                    new DeleteDirectoryStep(KeptPath, "Kept") { Identity = new ItemIdentity("kept", "Kept") },
                    new DeleteDirectoryStep(OfferedPath, "Offered") { Identity = new ItemIdentity("offered", "Offered") },
                    new DeleteDirectoryStep(AnonymousPath, "Anonymous"),
                    new RunCommandStep("tool", "clear", "Clear"),
                ],
                ProtectedPaths =
                [
                    new ProtectedPath(@"C:\Users\testuser\AppData\Local\example", "The root.", PresenceBefore: PathPresence.Present),
                ],
            };

            var keys = new KeepList(
                new[] { "KEPT", KeptPath, OfferedPath, AnonymousPath, "Anonymous", "tool clear", "clear" }
                    .Select(key => new KeptItem("example", "Example", new ItemIdentity(key, key))))
                .KeysFor("example");

            var kept = plan.WithKeepList(keys);

            Assert.All(kept.Steps, step => Assert.Contains(step, plan.Steps));
            Assert.Equal(plan.Steps.Count - 1, kept.Steps.Count);
            Assert.All(plan.ProtectedPaths, protection => Assert.Contains(protection, kept.ProtectedPaths));

            Assert.Equal([OfferedPath, AnonymousPath], kept.TargetedPaths);
            Assert.DoesNotContain(KeptPath, kept.NarrowedTo(plan.Steps).TargetedPaths);
            Assert.Empty(kept.ProofOnly().Steps);
        }
    }
}
