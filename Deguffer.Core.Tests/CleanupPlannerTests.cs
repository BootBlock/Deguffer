using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

public sealed class CleanupPlannerTests
{
    [Fact]
    public async Task SortsFindingsBySizeSoTheBiggestCauseLeads()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("small", bytes: 1_000),
            new StubProvider("large", bytes: 9_000),
            new StubProvider("medium", bytes: 5_000),
        ]);

        var findings = await planner.PlanAllAsync();

        Assert.Equal(["large", "medium", "small"], findings.Select(f => f.Provider.Id));
    }

    /// <summary>
    /// §5.5: never block on a complete scan. Each finding reaches the caller as it is produced, so
    /// the preview fills in rather than staying blank until the slowest provider finishes.
    /// </summary>
    [Fact]
    public async Task ReportsEachFindingAsItIsProducedRatherThanOnlyAtTheEnd()
    {
        var journal = new List<string>();
        var planner = new CleanupPlanner(
        [
            new StubProvider("first", bytes: 1_000, journal: journal),
            new StubProvider("second", bytes: 9_000, journal: journal),
        ]);

        var found = new ProgressRecorder<Finding>();

        var findings = await planner.PlanAllAsync(MinimumAge.Off, status: null, found, CancellationToken.None);

        // Reported in the order they were planned, not the order they are finally sorted into.
        Assert.Equal(["first", "second"], found.Reports.Select(f => f.Provider.Id));
        Assert.Equal(["second", "first"], findings.Select(f => f.Provider.Id));
    }

    /// <summary>
    /// The re-plan after a clean measures the rows the run changed and no others. A provider left out
    /// keeps its cached view as well as its finding, because nothing about its locations moved.
    /// </summary>
    [Fact]
    public async Task PlanningChosenProvidersTouchesNoOtherProvider()
    {
        var journal = new List<string>();
        var first = new StubProvider("first", bytes: 1_000, journal: journal);
        var second = new StubProvider("second", bytes: 9_000, journal: journal);
        var third = new StubProvider("third", bytes: 5_000, journal: journal);
        var planner = new CleanupPlanner([first, second, third]);

        var found = new ProgressRecorder<Finding>();

        var findings = await planner.PlanAsync([third, first], MinimumAge.Off, status: null, found, CancellationToken.None);

        Assert.Equal(["invalidate:third", "invalidate:first", "plan:third", "plan:first"], journal);
        Assert.Equal(["third", "first"], found.Reports.Select(f => f.Provider.Id));
        Assert.Equal(["third", "first"], findings.Select(f => f.Provider.Id));
    }

    [Fact]
    public async Task AnAbsentToolchainYieldsAFindingWithNoPlanRatherThanBeingDropped()
    {
        var planner = new CleanupPlanner(
            [new StubProvider("absent", bytes: 0, present: false)]);

        var finding = Assert.Single(await planner.PlanAllAsync());

        Assert.False(finding.IsPresent);
        Assert.Null(finding.Plan);
        Assert.False(finding.HasSomethingToRemove);
    }

    /// <summary>
    /// Nothing to remove and nothing to prove is not part of the run, at any tier. Above Tier 1 that
    /// includes not demanding an answer for a deletion that is not going to happen.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache)]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public async Task ExecuteSkipsFindingsWithNothingToDo(SafetyTier tier)
    {
        var empty = new StubProvider("empty", bytes: 0, tier: tier);
        var planner = new CleanupPlanner([empty]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Empty(results);
        Assert.False(empty.WasExecuted);
    }

    /// <summary>
    /// A plan whose every candidate was withheld has no steps and still makes a promise: the paths it
    /// protects will be standing afterwards. Dropping it from the run as having nothing to do left
    /// that promise with no evidence behind it, on the one run where the user's whole instruction
    /// was to leave something alone.
    /// </summary>
    [Fact]
    public async Task AStepFreePlanWithSomethingToProveIsStillVerified()
    {
        using var temp = new TempDirectory();
        var withheld = Directory.CreateDirectory(Path.Combine(temp.Path, "withheld")).FullName;

        var provider = new StubProvider("withheld", bytes: 0, protects: withheld);
        var planner = new CleanupPlanner([provider]);

        var result = Assert.Single(await planner.ExecuteAsync(await planner.PlanAllAsync()));

        Assert.True(provider.WasExecuted);
        Assert.NotNull(result.Verification);
        Assert.Equal(VerificationOutcome.Survived, Assert.Single(result.Verification.Checks).Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The case above is evidence only if it can fail. A survivor that is gone by the time the run
    /// verifies is reported, not passed over.
    /// </summary>
    [Fact]
    public async Task AStepFreePlanStillFailsVerificationWhenItsSurvivorIsGone()
    {
        using var temp = new TempDirectory();
        var withheld = Directory.CreateDirectory(Path.Combine(temp.Path, "withheld")).FullName;

        var planner = new CleanupPlanner([new StubProvider("withheld", bytes: 0, protects: withheld)]);
        var findings = await planner.PlanAllAsync();

        Directory.Delete(withheld);

        var result = Assert.Single(await planner.ExecuteAsync(findings));

        Assert.False(result.Verification!.Passed);
        Assert.Equal(withheld, Assert.Single(result.Verification.Failures).Subject);
    }

    /// <summary>
    /// A protected path that was not there when the plan was made proves nothing by being absent
    /// afterwards, so a plan holding only those has nothing to prove and stays out of the run.
    /// </summary>
    [Fact]
    public async Task ASurvivorThatWasNeverThereIsNothingToProve()
    {
        using var temp = new TempDirectory();
        var provider = new StubProvider("absent", bytes: 0, protects: Path.Combine(temp.Path, "never-made"));
        var planner = new CleanupPlanner([provider]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Empty(results);
        Assert.False(provider.WasExecuted);
    }

    /// <summary>
    /// A plan with no steps whose only protection is one a rule applies — a tool root, an
    /// unrecognised sibling — has nothing of its own to prove. That protection guards against the
    /// plan's own deletion, and there is none, so running it would name an already-clear location in
    /// the run's verdict as though it had been cleaned.
    /// </summary>
    [Fact]
    public async Task AStepFreePlanProtectingOnlyByRuleStaysOutOfTheRun()
    {
        using var temp = new TempDirectory();
        var provider = new StubProvider("clear", bytes: 0, protects: temp.Path, protectsByRule: true);
        var planner = new CleanupPlanner([provider]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Empty(results);
        Assert.False(provider.WasExecuted);
    }

    /// <summary>
    /// A run holding nothing but verification still moves the bar, one share each. Giving them no share
    /// there would leave the bar with nothing to divide by.
    /// </summary>
    [Fact]
    public async Task ARunOfNothingButVerificationSharesTheBarBetweenThem()
    {
        using var temp = new TempDirectory();
        var planner = new CleanupPlanner(
        [
            new StubProvider("first", bytes: 0, protects: temp.CreateDirectory("first")),
            new StubProvider("second", bytes: 0, protects: temp.CreateDirectory("second")),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        Assert.Equal([0.5, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// A plan with no steps destroys nothing, so §7 has nothing to authorise. Demanding an answer
    /// would throw the whole run away over a check that only reads the disk. Tier 4 is included on
    /// purpose: what is refused there is a deletion, and this plan holds none.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public async Task AStepFreePlanNeedsNoConfirmationToBeVerified(SafetyTier tier)
    {
        using var temp = new TempDirectory();
        var provider = new StubProvider("withheld", bytes: 0, tier: tier, protects: temp.Path);
        var planner = new CleanupPlanner([provider]);

        var result = Assert.Single(await planner.ExecuteAsync(await planner.PlanAllAsync()));

        Assert.True(provider.WasExecuted);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A step-free plan is verified after every plan that deletes, whatever order the selection
    /// arrived in. Its promise is about what the whole run leaves standing, and proving it before a
    /// later plan has deleted anything would prove nothing about that plan. It takes no share of the
    /// bar either, because the part of a run that takes any time is the deleting.
    /// </summary>
    [Fact]
    public async Task AStepFreePlanIsVerifiedLastAndTakesNoShareOfTheBar()
    {
        using var temp = new TempDirectory();
        List<string> journal = [];
        var planner = new CleanupPlanner(
        [
            new StubProvider("withheld", bytes: 0, journal: journal, protects: temp.Path),
            new StubProvider("work", bytes: 1_000, journal: journal),
        ]);

        var findings = await planner.PlanAllAsync();
        journal.Clear();
        var progress = new ProgressRecorder<double>();

        // Reversed, so the plan with nothing to delete arrives first.
        await planner.ExecuteAsync([.. findings.OrderBy(f => f.EstimatedBytes)], progress: progress);

        Assert.Equal(["execute:work", "execute:withheld"], journal);
        Assert.Equal([1.0, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// The share of nothing has to hold beside a plan carrying no estimate as well. Weighed together,
    /// the two have no figure between them and fall back to one share each, which would give the
    /// verification half the bar and leave it parked there for as long as the command runs.
    /// </summary>
    [Fact]
    public async Task AStepFreePlanTakesNoShareBesideAPlanWithNoEstimate()
    {
        using var temp = new TempDirectory();
        var planner = new CleanupPlanner(
        [
            new StubProvider("withheld", bytes: 0, protects: temp.Path),
            new StubProvider("command", bytes: 0, planStepWithoutEstimate: true),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        Assert.Equal([1.0, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public async Task RefusesToExecuteAboveTier1WithoutTheConfirmationSection7Requires(SafetyTier tier)
    {
        // The failure that matters is a caller that simply forgot to ask: it must fail closed.
        var provider = new StubProvider("risky", bytes: 5_000, tier: tier);
        var planner = new CleanupPlanner([provider]);

        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(() => planner.ExecuteAsync(findings));
        Assert.False(provider.WasExecuted);
    }

    [Fact]
    public async Task ExecutesTier2OnceAcknowledged()
    {
        var provider = new StubProvider("android", bytes: 5_000, tier: SafetyTier.RegenerableWithCost);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        var results = await planner.ExecuteAsync(findings, [new Confirmation("android")]);

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// A confirmation names its subject. Acknowledging one provider must not authorise a different
    /// one that happened to be selected in the same pass.
    /// </summary>
    [Fact]
    public async Task AConfirmationForAnotherProviderDoesNotAuthoriseThisOne()
    {
        var provider = new StubProvider("android", bytes: 5_000, tier: SafetyTier.RegenerableWithCost);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("platformio")]));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>§7: Tier 3 requires *typed* confirmation — a bare acknowledgement is not enough.</summary>
    [Fact]
    public async Task Tier3NeedsTheTypedPhraseNotMerelyAnAcknowledgement()
    {
        var provider = new StubProvider("workspace-state", bytes: 5_000, tier: SafetyTier.UserData);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("workspace-state")]));
        Assert.False(provider.WasExecuted);

        // The StubProvider's Name is its id, so that is the phrase the requirement asks for.
        var results = await planner.ExecuteAsync(
            findings, [new Confirmation("workspace-state", "workspace-state")]);

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// §3 excludes Tier 4 from the UI entirely, so no answer authorises it — including a correctly
    /// typed phrase, which is the route by which a Tier 4 row offered in error would get executed.
    /// </summary>
    [Fact]
    public async Task NoConfirmationAuthorisesTier4()
    {
        var provider = new StubProvider("credentials", bytes: 5_000, tier: SafetyTier.DoNotTouch);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, [new Confirmation("credentials", "credentials")]));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>Tier 1 is unaffected: selecting the row remains the whole decision.</summary>
    [Fact]
    public async Task Tier1StillExecutesWithNoConfirmationAtAll()
    {
        var provider = new StubProvider("npm", bytes: 5_000);
        var planner = new CleanupPlanner([provider]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// Every provider above Tier 1 is named here individually, in both directions.
    ///
    /// This replaced a blanket assertion that nothing above Tier 2 shipped, which the Recycle Bin
    /// provider retired. Widening that to <c>&lt;= UserData</c> would have left the gate asserting
    /// almost nothing, so what it checks now is membership: a provider that changes tier fails,
    /// and so does a new one that arrives at Tier 3 without anybody deciding it should. Tier 3 is
    /// irreversible loss of user data, so arriving there is a decision, never a default.
    /// </summary>
    /// <summary>
    /// Every row that owns entries of a temporary folder is handed to the temporary-folder row. One
    /// left out has its entries counted twice, and a file it keeps — Blender's unsaved work — taken
    /// there on its age.
    /// </summary>
    [Fact]
    public void TheTemporaryFolderRowLeavesEveryTenantsEntriesToIt()
    {
        var planner = CleanupPlanner.CreateDefault();
        var temporary = Assert.Single(planner.Providers.OfType<TempDirectoryProvider>());
        var tenants = planner.Providers.OfType<ITemporaryFolderTenant>().ToList();

        Assert.NotEmpty(tenants);
        Assert.Equal(
            tenants.Select(t => t.Name).Order(StringComparer.Ordinal),
            temporary.Tenants.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(temporary.Tenants, t => Assert.Contains(t, tenants));
    }

    [Fact]
    public void TheDefaultSetIsTheVerifiedSourcesAndEveryTierAboveOneIsNamed()
    {
        var planner = CleanupPlanner.CreateDefault();

        Assert.Equal(
            [
                "dotnet-obj", "unity-library", "unreal-intermediate", "unreal-project-ddc", "cargo-target", "node-modules", "python-venv",
                "nuget", "gradle", "npm", "pnpm", "vscode-cpptools", "dart-analysis-server", "roslyn-cache", "temp-tool-caches",
                "uv", "pip", "poetry", "conda", "cargo", "go", "zig", "maven", "vcpkg", "gpu-shader-cache",
                "chromium-app-cache", "vscode-cache", "firefox", "epic-launcher-webcache",
                "epic-launcher-content-cache", "battle-net-cache", "steam", "steam-library-artwork", "steam-shader-cache", "emulator-shader-cache", "retroarch-downloads", "retroarch-thumbnails", "unreal-ddc", "spotify",
                "plex-transcode", "jellyfin-transcode", "emby-transcode", "affinity-model-cache", "capture-one-cache", "davinci-resolve-render-cache", "after-effects-disk-cache", "adobe-media-cache",
                "squirrel-staging",
                "platformio", "playwright", "puppeteer", "lmstudio-runtimes", "test-browser-profiles", "squirrel-superseded-versions", "azure-functions-tools",
                "graphics-driver-installers", "autodesk-installers", "claude-code-leftovers", "recycle-bin", "file-history", "cloud-local-copies", "temp-directories",
                "temp-installer-downloads", "delivery-optimization", "previous-windows-installation", "windows-update-leftovers",
                "driver-store", "crash-dumps",
                "windows-servicing-logs", "epic-launcher-logs", "battle-net-logs", "vscode-logs", "claude-code-mcp-logs", "temp-tool-logs",
                "claude-code-file-history", "claude-code-conversations",
            ],
            planner.Providers.Select(p => p.Id));

        Assert.Equal(
            [
                "unity-library", "unreal-intermediate", "unreal-project-ddc", "cargo-target", "node-modules", "python-venv",
                "conda", "maven", "vcpkg", "steam-library-artwork", "steam-shader-cache", "emulator-shader-cache", "retroarch-downloads", "unreal-ddc", "affinity-model-cache", "capture-one-cache", "davinci-resolve-render-cache", "after-effects-disk-cache", "adobe-media-cache", "platformio", "playwright", "puppeteer", "lmstudio-runtimes",
                "squirrel-superseded-versions", "azure-functions-tools", "graphics-driver-installers", "autodesk-installers",
                "cloud-local-copies", "temp-directories", "temp-installer-downloads", "previous-windows-installation",
                "windows-update-leftovers", "driver-store",
            ],
            planner.Providers.Where(p => p.Tier == SafetyTier.RegenerableWithCost).Select(p => p.Id));

        Assert.Equal(
            [
                "retroarch-thumbnails", "recycle-bin", "file-history", "crash-dumps", "windows-servicing-logs",
                "epic-launcher-logs", "battle-net-logs", "vscode-logs", "claude-code-mcp-logs", "temp-tool-logs", "claude-code-file-history",
                "claude-code-conversations",
            ],
            planner.Providers.Where(p => p.Tier == SafetyTier.UserData).Select(p => p.Id));

        // §3 excludes Tier 4 from the UI entirely, so a provider declaring it could only ever
        // produce a row no confirmation can authorise.
        Assert.DoesNotContain(planner.Providers, p => p.Tier == SafetyTier.DoNotTouch);
    }

    [Fact]
    public async Task InvalidatesEveryProviderBeforeAnyOfThemPlans()
    {
        // Ordering matters: invalidating inside the planning loop would throw away the machine
        // snapshot the previous provider just paid for, since providers share collaborators.
        List<string> journal = [];
        var planner = new CleanupPlanner(
            [new StubProvider("a", 1, journal: journal), new StubProvider("b", 2, journal: journal)]);

        await planner.PlanAllAsync();

        Assert.Equal(["invalidate:a", "invalidate:b", "plan:a", "plan:b"], journal);
    }

    /// <summary>
    /// Every default provider reads the machine through the environment the planner is built over.
    ///
    /// <para>Explore builds each policy over a fresh environment, so where a command is on
    /// <c>PATH</c> is looked up at the build rather than remembered by the shared one since the last
    /// Storage pass (issue #120). A provider constructed without it falls back to
    /// <see cref="UserEnvironment.Current"/>, and Explore then answers for that one provider from the
    /// old lookup. Observed through invalidation, because
    /// <see cref="CleanupProviderBase.InvalidateCaches"/> clears exactly the environment a provider
    /// resolves commands through, and every default provider derives from it.</para>
    /// </summary>
    [Fact]
    public void EveryDefaultProviderReadsTheMachineThroughTheEnvironmentItIsGiven()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var planner = CleanupPlanner.CreateDefault(
            liveTrees: FakeLiveTreeInspector.NothingLive,
            environment: environment);

        foreach (var provider in planner.Providers)
        {
            var before = environment.InvalidateCount;

            provider.InvalidateCaches();

            Assert.True(
                environment.InvalidateCount > before,
                $"'{provider.Id}' reads the machine through an environment other than the one it was given.");
        }
    }

    /// <summary>
    /// The default is §7's strict rule, so a caller that never mentions the preference still gets
    /// the typed phrase demanded of it. Forgetting to pass it has to fail closed, not open.
    /// </summary>
    [Fact]
    public async Task Tier3DemandsTheTypedPhraseWhenTheCallerSaysNothingAboutThePreference()
    {
        var provider = new StubProvider("workspace-state", bytes: 5_000, tier: SafetyTier.UserData);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(() => planner.ExecuteAsync(findings));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>
    /// With the typed phrase switched off, the planner accepts a Tier 3 plan with no answer at all.
    /// Both halves have to move together: a shell that stops asking against a planner that still
    /// demands an answer turns the preference into a refusal to clean.
    /// </summary>
    [Fact]
    public async Task Tier3ExecutesWithNoAnswerWhenTheTypedPhraseIsSwitchedOff()
    {
        var provider = new StubProvider("workspace-state", bytes: 5_000, tier: SafetyTier.UserData);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        var results = await planner.ExecuteAsync(findings, requireTypedPhrase: false);

        Assert.Single(results);
        Assert.True(provider.WasExecuted);
    }

    /// <summary>
    /// §3 keeps Tier 4 out of the UI however the user has set the preference. A setting about how
    /// hard Tier 3 is to authorise must never become an authorisation for the tier above it.
    /// </summary>
    [Fact]
    public async Task NoConfirmationAuthorisesTier4EvenWithTheTypedPhraseSwitchedOff()
    {
        var provider = new StubProvider("credentials", bytes: 5_000, tier: SafetyTier.DoNotTouch);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(
                findings, [new Confirmation("credentials", "credentials")], requireTypedPhrase: false));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>Tier 2 keeps its acknowledgement: the preference is about typing, not about asking.</summary>
    [Fact]
    public async Task Tier2StillNeedsItsAcknowledgementWhenTheTypedPhraseIsSwitchedOff()
    {
        var provider = new StubProvider("android", bytes: 5_000, tier: SafetyTier.RegenerableWithCost);
        var planner = new CleanupPlanner([provider]);
        var findings = await planner.PlanAllAsync();

        await Assert.ThrowsAsync<ConfirmationRequiredException>(
            () => planner.ExecuteAsync(findings, requireTypedPhrase: false));
        Assert.False(provider.WasExecuted);
    }

    /// <summary>
    /// A provider absent only for want of an approved folder is still asked for its plan, because
    /// that plan is the only place it says which folder to add. It was not, and the sentence was
    /// unreachable: presence and "nothing approved yet" were the same answer, so the guidance sat
    /// behind a branch that ran only when the guidance was not needed.
    ///
    /// <see cref="Finding.IsPresent"/> still reports the truth. It is the plan that is now asked
    /// for regardless, not the presence that is faked.
    /// </summary>
    [Fact]
    public async Task AsksAProviderAwaitingSourceFoldersForItsPlanEvenThoughItIsAbsent()
    {
        var journal = new List<string>();
        var planner = new CleanupPlanner(
            [new StubProvider("build-output", bytes: 0, present: false, journal: journal, awaitingSourceFolders: true)]);

        var findings = await planner.PlanAllAsync();

        Assert.Contains("plan:build-output", journal);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsPresent);
        Assert.NotNull(finding.Plan);
    }

    /// <summary>
    /// The negative of the case above, and the one that keeps the short circuit worth having: a
    /// toolchain that is simply not installed is never asked to plan, so a machine without it pays
    /// nothing for the provider being registered.
    /// </summary>
    [Fact]
    public async Task DoesNotPlanForAToolchainThatIsSimplyAbsent()
    {
        var journal = new List<string>();
        var planner = new CleanupPlanner([new StubProvider("gone", bytes: 0, present: false, journal: journal)]);

        var findings = await planner.PlanAllAsync();

        Assert.DoesNotContain("plan:gone", journal);
        Assert.Null(Assert.Single(findings).Plan);
    }

    /// <summary>
    /// The bar is weighted by what each plan will free, not by how many plans there are. A 40 GB
    /// cache and a 5 MB one are one plan each, so an equal split would park the bar half way
    /// through for the whole of the part that takes any time.
    /// </summary>
    [Fact]
    public async Task WeightsTheBarByWhatEachPlanFreesRatherThanByHowManyThereAre()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("large", bytes: 9_000),
            new StubProvider("small", bytes: 1_000),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        Assert.Equal([0.9, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// A provider reporting about itself reports 0 to 1, and what reaches the caller is that
    /// provider's share. Without the offset the first plan would drive the bar to the end and
    /// every plan after it would drive it there again.
    /// </summary>
    [Fact]
    public async Task AProvidersOwnFractionArrivesAsItsShareOfTheWholeRun()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("large", bytes: 9_000, reports: [0.5]),
            new StubProvider("small", bytes: 1_000, reports: [0.5]),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        // Half way through nine tenths of the run is 45%; half way through the last tenth is 95%.
        Assert.Equal([0.45, 0.9, 0.95, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// A selected row with nothing to remove is not part of the run and takes no share of the bar.
    /// Counting it would give half the bar to a finding that completes instantly, which is the
    /// same misdescription weighting by size exists to avoid.
    /// </summary>
    [Fact]
    public async Task APlanWithNothingToRemoveTakesNoShareOfTheBar()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("has-work", bytes: 1_000),
            new StubProvider("already-clear", bytes: 0),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        Assert.Equal([1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// The run made entirely of command steps whose own tool reports no figure. There is nothing to
    /// weight by, so the count is the best answer available — and it is a real answer rather than a
    /// division by zero.
    /// </summary>
    [Fact]
    public async Task SharesTheBarEquallyWhenNoPlanCarriesAnEstimate()
    {
        var planner = new CleanupPlanner(
        [
            new StubProvider("first", bytes: 0, planStepWithoutEstimate: true),
            new StubProvider("second", bytes: 0, planStepWithoutEstimate: true),
        ]);

        var findings = await planner.PlanAllAsync();
        var progress = new ProgressRecorder<double>();

        await planner.ExecuteAsync(findings, progress: progress);

        Assert.Equal([0.5, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// Nothing selected is the one input whose weights sum to zero, so it is the one that would
    /// divide by it. The reason it does not is structural rather than guarded: every division sits
    /// inside the loop over those same parts, which an empty selection never enters. This pins that
    /// structure, because a NaN reaching the bar leaves it unpaintable rather than merely wrong.
    /// </summary>
    [Fact]
    public async Task AnEmptySelectionReportsNothingRatherThanDividingByZero()
    {
        var progress = new ProgressRecorder<double>();

        var results = await new CleanupPlanner([]).ExecuteAsync([], progress: progress);

        Assert.Empty(results);
        Assert.Empty(progress.Reports);
    }

    /// <summary>
    /// §5.6's negative asks whether Deguffer took a protected path that has gone missing, and a run
    /// is many plans. A provider handed only its own targets would find the folder the provider
    /// beside it deleted indistinguishable from a folder a stranger deleted, and report Deguffer's
    /// own deletion as somebody else's — the one direction that check must never fail in. So each
    /// provider is told what the whole run will destroy, not what it will destroy itself.
    /// </summary>
    [Fact]
    public async Task EveryProviderIsToldWhatTheWholeRunMayDestroy()
    {
        var deleter = new StubProvider("obj", bytes: 4_000, deletes: @"C:\Users\testuser\src\project\obj");
        var evictor = new StubProvider("nuget", bytes: 9_000);
        var planner = new CleanupPlanner([deleter, evictor]);

        await planner.ExecuteAsync(await planner.PlanAllAsync());

        // The delete-only provider learns both halves from the run rather than from its own plan:
        // the other provider's paths, and that §5.1's eviction command makes the reach unbounded.
        Assert.NotNull(deleter.ReachHandedOver);
        Assert.True(deleter.ReachHandedOver.Unbounded);
        Assert.Equal(deleter.ReachHandedOver, evictor.ReachHandedOver);
        Assert.Contains(@"C:\Users\testuser\src\project\obj", deleter.ReachHandedOver.TargetedPaths);
    }

    /// <summary>
    /// What one plan's removal left standing is evidence for every plan verified after it. A folder one
    /// provider's removal went into may be a folder another provider promised to leave, so every
    /// provider is handed the one record the whole run writes to — and the plan proving its survivors
    /// last reads what the plan before it left inside one.
    /// </summary>
    [Fact]
    public async Task APlanVerifiedLaterSeesWhatAnEarlierPlansRemovalLeftInsideItsFolder()
    {
        using var temp = new TempDirectory();
        var scratch = temp.CreateDirectory("scratch");
        var kept = temp.CreateDirectory("scratch", "kept");
        var working = temp.CreateDirectory("scratch", "kept", "work");

        var clearing = new StubProvider("clear", bytes: 1_000, deletes: scratch, leavesStanding: [working]);
        var keeping = new StubProvider("keep", bytes: 0, protects: kept);
        var planner = new CleanupPlanner([clearing, keeping]);

        var results = await planner.ExecuteAsync(await planner.PlanAllAsync());

        Assert.NotNull(clearing.ResidueHandedOver);
        Assert.Same(clearing.ResidueHandedOver, keeping.ResidueHandedOver);

        var check = Assert.Single(results.Single(r => r.ProviderId == "keep").Verification!.Checks);

        Assert.Equal(kept, check.Subject);
        Assert.Equal(VerificationOutcome.Entered, check.Outcome);
    }

    private sealed class StubProvider(
        string id,
        long bytes,
        bool present = true,
        SafetyTier tier = SafetyTier.RegenerableCache,
        List<string>? journal = null,
        bool awaitingSourceFolders = false,
        IReadOnlyList<double>? reports = null,
        bool planStepWithoutEstimate = false,
        string? deletes = null,
        string? protects = null,
        bool protectsByRule = false,
        IReadOnlyList<string>? leavesStanding = null) : ICleanupProvider
    {
        public bool IsAwaitingSourceFolders => awaitingSourceFolders;

        public IReadOnlyList<ToolRoot> ToolRoots => [];

        public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolRoot>>([]);

        public bool WasExecuted { get; private set; }

        /// <summary>What the planner said the whole run may destroy, for §5.6's negative.</summary>
        public RunReach? ReachHandedOver { get; private set; }

        /// <summary>The record of what the run's removals left standing, as the planner handed it over.</summary>
        public RunResidue? ResidueHandedOver { get; private set; }

        public void InvalidateCaches() => journal?.Add($"invalidate:{id}");

        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => tier;

        public StepGrain Grain => StepGrain.Parts;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderDescription Description { get; } = new()
        {
            Application = "A stub, standing in for a real toolchain.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(present);

        public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default)
        {
            journal?.Add($"plan:{id}");
            return Task.FromResult(NewPlan());
        }

        private CleanupPlan NewPlan() => new()
        {
            ProviderId = id,
            ProviderName = id,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = (bytes, planStepWithoutEstimate, deletes) switch
            {
                (0, false, null) => [],

                // A path rather than a command, for the run-reach assertions: §5.1's command step
                // contributes no target and makes the whole run unbounded, so a plan built from one
                // cannot show what a delete-only provider is told about the run around it.
                (_, _, { } path) =>
                    [new DeleteDirectoryStep(path, "Output") { Estimated = new ScanSize(bytes, bytes) }],

                _ => [new RunCommandStep("tool", "clear", "Clear") { Estimated = new ScanSize(bytes, bytes) }],
            },
            ProtectedPaths = protects is null
                ? []
                :
                [
                    new ProtectedPath(
                        protects,
                        "Withheld from this plan.",
                        PresenceBefore: LongPath.ProbeDirectory(protects),
                        Withheld: protectsByRule ? Withholding.None : Withholding.TooRecent),
                ],
        };

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            WasExecuted = true;
            ReachHandedOver = runReach;
            ResidueHandedOver = residue;
            journal?.Add($"execute:{id}");

            // Stands in for what a real removal records about the folders it could not take.
            if (deletes is not null && leavesStanding is not null)
            {
                residue?.Record(deletes, leavesStanding);
            }

            // Stands in for the fractions a real removal emits as it works through a tree.
            foreach (var fraction in reports ?? [])
            {
                progress?.Report(fraction);
            }

            // The real verifier rather than an empty result, so a test can tell a plan that was
            // verified from one that was merely handed over.
            return Task.FromResult(new CleanupResult
            {
                ProviderId = id,
                ProviderName = id,
                Verification = PlanVerifier.Verify(plan, runReach, residue, ct),
            });
        }

        public Task<VerificationResult> VerifyAsync(
            CleanupPlan plan, RunReach? runReach = null, CancellationToken ct = default) =>
            Task.FromResult(new VerificationResult());
    }
}
