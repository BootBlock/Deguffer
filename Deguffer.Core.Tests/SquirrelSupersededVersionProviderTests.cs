using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider that removes an installed program rather than something a program wrote, so
/// the assertions are about what it refuses far more than about what it takes.
///
/// <para>Four things have to hold. The build in use is never a target, however many are on disk. An
/// installation holding a version number Deguffer cannot order gives up nothing at all, because
/// "which one is current?" then has no answer. An application that is running is refused outright
/// rather than warned about. And a folder that fails either half of the identification test is
/// never reached into.</para>
/// </summary>
public sealed class SquirrelSupersededVersionProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SquirrelSupersededVersionProviderTests() =>
        _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SquirrelSupersededVersionProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(
            _environment,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning);

    /// <summary>A directory with a file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    /// <summary>
    /// An application Squirrel installed: the updater beside every one it manages, a directory per
    /// build, and the packages folder it updates from.
    /// </summary>
    private string CreateApplication(string name, params string[] versions)
    {
        var root = Path.Combine(_environment.LocalAppData, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        Populate(Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName));

        foreach (var version in versions)
        {
            Populate(Path.Combine(root, "app-" + version));
        }

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSquirrelApplication()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Three builds on disk, and the newest is the one the application launches. Ordering is by
    /// version rather than by name or by date, which is the rule the application's own shim uses.
    /// </summary>
    [Fact]
    public async Task PlansEveryBuildExceptTheNewest()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4", "3.10.0");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Path.Combine(root, "app-3.6.3"), Path.Combine(root, "app-3.6.4") }
                .Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    /// <summary>
    /// Each build is listed under its application and told apart by its number, so a machine holding
    /// a dozen Squirrel applications reads as a dozen headings rather than one list of folders.
    /// </summary>
    [Fact]
    public async Task ListsEachBuildUnderItsApplicationWithItsVersion()
    {
        var chatterbox = CreateApplication("Chatterbox", "3.6.3", "3.10.0");
        var notekeeper = CreateApplication("Notekeeper", "1.0.0", "1.1.0");

        var steps = (await CreateProvider().PlanAsync()).Steps
            .OfType<DeleteStep>()
            .ToDictionary(step => step.Path, StringComparer.OrdinalIgnoreCase);

        var superseded = steps[Path.Combine(chatterbox, "app-3.6.3")];
        Assert.Equal("Chatterbox", superseded.Group);
        Assert.Equal([new ItemFacet("Version", "3.6.3")], superseded.Facets);

        Assert.Equal("Notekeeper", steps[Path.Combine(notekeeper, "app-1.0.0")].Group);
    }

    /// <summary>An application holding one build has nothing to give up, and says so plainly.</summary>
    [Fact]
    public async Task AnApplicationHoldingOneBuildOffersNothing()
    {
        CreateApplication("Chatterbox", "3.6.4");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("one build and no more", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6's negative, and the one that matters most here: the build in use, the updater, the
    /// packages folder and the application's own folder all survive a run that removed the build it
    /// replaced.
    /// </summary>
    [Fact]
    public async Task TheBuildInUseTheUpdaterAndThePackagesAllSurvive()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        string[] directories =
        [
            root,
            Path.Combine(root, "app-3.6.4"),
            Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName),
        ];

        var updater = Path.Combine(root, SquirrelDiscovery.UpdaterName);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(root, "app-3.6.3"), Assert.Single(plan.TargetedPaths));

        foreach (var path in directories.Append(updater))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(directories, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.True(File.Exists(updater), $"{updater} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(root, "app-3.6.3")));
    }

    /// <summary>
    /// G8's unrecognised case, and here it decides which build is the running one. A pre-release
    /// version orders below its own release under one reading and above it under another, so an
    /// installation holding one gives up nothing — the alternative is to name the build in use as
    /// superseded and remove the application out from under the user.
    /// </summary>
    [Theory]
    [InlineData("3.7.0-beta1")]
    [InlineData("preview")]
    public async Task AVersionNumberItCannotOrderLeavesEveryBuildAlone(string unreadable)
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var odd = Populate(Path.Combine(root, "app-" + unreadable));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == odd && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(root, "app-3.6.3")), "an older build was removed");
    }

    /// <summary>
    /// §5.3, and here it is a refusal rather than a warning. The process holding the application
    /// open runs from the build it did <em>not</em> supersede, so the question this answers is
    /// whether the application is running at all — not whether the old directory itself is busy.
    /// </summary>
    [Fact]
    public async Task AnApplicationThatIsRunningGivesUpNothing()
    {
        var running = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var idle = CreateApplication("Notepad", "1.0", "1.1");

        var provider = CreateProvider(new FakeLiveTreeInspector(running));
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(idle, "app-1.0"), Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Chatterbox", StringComparison.Ordinal));
        Assert.Contains(
            plan.ProtectedPaths,
            p => p.Path == Path.Combine(running, "app-3.6.3") && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(
            Directory.Exists(Path.Combine(running, "app-3.6.3")),
            "a build was removed while the application was running");
    }

    /// <summary>
    /// A build somebody moved onto another drive with a link. It still counts when the versions are
    /// ordered — dropping it is how the newest build gets named superseded — and it is never
    /// removed: what it points at was never classified, and the figure beside it would have been
    /// measured through it.
    /// </summary>
    [Fact]
    public async Task ALinkedBuildIsCountedWhenOrderingAndNeverRemoved()
    {
        var root = CreateApplication("Chatterbox", "3.6.3");
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));

        // The newest build, and a link. Ordering has to see it, or app-3.6.3 becomes the newest and
        // the running build is the one offered.
        SymbolicLink.ToDirectory(Path.Combine(root, "app-3.6.4"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(root, "app-3.6.3"), Assert.Single(plan.TargetedPaths));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
    }

    /// <summary>
    /// The same link, this time on a build that <em>is</em> superseded. It is named, left alone and
    /// asserted to survive, on the rule every other root in this change applies.
    /// </summary>
    [Fact]
    public async Task ASupersededBuildThatIsALinkIsLeftAloneAndReported()
    {
        var root = CreateApplication("Chatterbox", "3.6.4");
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));

        var link = Path.Combine(root, "app-3.6.3");
        SymbolicLink.ToDirectory(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link && p.PresenceBefore is PathPresence.Present);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
        Assert.False(provider.ToolRoots[0].RecognisesFolder("app-3.6.3"));
    }

    /// <summary>
    /// §5.6 where nothing is removed at all. An installation nobody could order gives up nothing, so
    /// every build it holds has to be asserted — an assertion covering only the current build proves
    /// nothing about the folder, and there is no current build to assert in that case.
    /// </summary>
    [Fact]
    public async Task EveryBuildOfAnInstallationThatCannotBeOrderedIsAsserted()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var odd = Populate(Path.Combine(root, "app-3.7.0-beta1"));

        string[] mustSurvive =
        [
            Path.Combine(root, "app-3.6.3"),
            Path.Combine(root, "app-3.6.4"),
            odd,
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        foreach (var path in mustSurvive)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(mustSurvive, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A check that could not run must not look like a check that found nothing (§5.5's reasoning,
    /// applied to a safeguard rather than a measurement).
    /// </summary>
    [Fact]
    public async Task AMachineWhereLivenessCannotBeEstablishedSaysSo()
    {
        CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check", StringComparison.Ordinal));
    }

    /// <summary>
    /// Identification is the updater <em>and</em> a build directory whose version can be read. A
    /// folder with only one of them is not established as a Squirrel application, so nothing in it
    /// is ever offered.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AFolderMissingHalfTheIdentificationIsNeverReachedInto(bool updater, bool builds)
    {
        var root = Path.Combine(_environment.LocalAppData, "NotSquirrel");
        Directory.CreateDirectory(root);

        if (updater)
        {
            File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        }

        if (builds)
        {
            Populate(Path.Combine(root, "app-1.0.0"));
            Populate(Path.Combine(root, "app-1.1.0"));
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(provider.ToolRoots);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        if (builds)
        {
            Assert.True(Directory.Exists(Path.Combine(root, "app-1.0.0")), "a build was removed");
        }
    }

    /// <summary>
    /// §5.2 as §7.1 reads it. Explore refuses the application's folder, its updater and the build in
    /// use, and allows only a build the application has replaced — which is the one thing the
    /// Storage page offers here.
    /// </summary>
    [Fact]
    public void TheDeclarationRefusesEverythingExceptASupersededBuild()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        var declaration = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(root, declaration.Path);
        Assert.True(declaration.RecognisesFolder("app-3.6.3"));
        Assert.False(declaration.RecognisesFolder("app-3.6.4"));
        Assert.False(declaration.RecognisesFolder(SquirrelDiscovery.UpdaterName));
        Assert.False(declaration.RecognisesFolder(SquirrelDiscovery.PackagesDirectoryName));
        Assert.False(declaration.RecognisesFolder("app.ico"));
    }

    /// <summary>
    /// G4: the profile is swept once for the life of a planning pass, and again after an
    /// invalidation, so an application updated while Deguffer was open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheProfileIsSweptOncePerPassAndAgainAfterInvalidation()
    {
        CreateApplication("Chatterbox", "3.6.4");

        var provider = CreateProvider();

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        var older = Populate(Path.Combine(_environment.LocalAppData, "Chatterbox", "app-3.6.3"));

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal(older, Assert.Single((await provider.PlanAsync()).TargetedPaths));
    }

    /// <summary>
    /// §7.1 over the builds a running application superseded. The name-shaped declaration recognises
    /// them and the plan holds every one back while the application runs, so Explore refuses them by
    /// what is running: at the build, where the declaration over the installation cannot outvote it.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheOlderBuildsOfAnApplicationThatIsRunning()
    {
        var running = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var idle = CreateApplication("Notepad", "1.0", "1.1");
        var superseded = Path.Combine(running, "app-3.6.3");

        var provider = CreateProvider(new FakeLiveTreeInspector(running));
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [provider]);

        Assert.Contains(provider.ToolRoots, r =>
            r.Path.Equals(running, StringComparison.OrdinalIgnoreCase) && r.RecognisesFolder("app-3.6.3"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == superseded);

        var refusal = policy.MayRemove(superseded);
        Assert.False(refusal.IsAllowed);
        Assert.Contains("Chatterbox is running", refusal.Reason, StringComparison.Ordinal);

        Assert.True(policy.MayRemove(Path.Combine(idle, "app-1.0")).IsAllowed);
    }

    /// <summary>
    /// An index in Squirrel's own format naming <paramref name="packages"/>, which is what a shortcut
    /// that runs <c>Update.exe --processStart</c> reads to choose a build.
    /// </summary>
    private static string WriteIndex(string root, params string[] packages)
    {
        var index = Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName, SquirrelReleaseIndex.FileName);

        File.WriteAllText(index, string.Join("\n", packages.Select(p => $"{new string('A', 40)} {p} 2048")));

        return index;
    }

    /// <summary>
    /// Every build an installation holds, asserted to survive a run of <paramref name="plan"/>, with
    /// the run's own §5.6 verification passing.
    /// </summary>
    private static async Task AssertEveryBuildSurvives(
        SquirrelSupersededVersionProvider provider, CleanupPlan plan, params string[] builds)
    {
        foreach (var build in builds)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(build, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(builds, b => Assert.True(Directory.Exists(b), $"{b} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// An update that stopped while it unpacked: the updater writes its marker into the new build
    /// first, so the stub never starts it, and the index still names the build before it. Ordering by
    /// version alone calls the unfinished build current and offers the one both launch paths start.
    /// A marker in an older build is what a later update leaves when its clean-up fails, and the
    /// stub will not start that build either. The stub skips a build on any entry by the marker's
    /// name, so a directory counts as well as a file.
    /// </summary>
    [Theory]
    [InlineData("2.0.0", false)]
    [InlineData("1.5.0", false)]
    [InlineData("2.0.0", true)]
    public async Task AnUnfinishedUpdateLeavesEveryBuildAlone(string unfinished, bool directory)
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        WriteIndex(root, "Chatterbox-1.5.0-full.nupkg");
        var marker = Path.Combine(root, "app-" + unfinished, SquirrelDiscovery.UnfinishedMarkerName);

        if (directory)
        {
            Directory.CreateDirectory(marker);
        }
        else
        {
            File.WriteAllText(marker, string.Empty);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("stopped before it finished", StringComparison.Ordinal));
        Assert.False(Assert.Single(provider.ToolRoots).RecognisesFolder("app-1.5.0"));

        await AssertEveryBuildSurvives(
            provider, plan, Path.Combine(root, "app-1.5.0"), Path.Combine(root, "app-2.0.0"));
    }

    /// <summary>
    /// A marker Windows would not describe is not a marker that is absent: the build may be one the
    /// stub refuses to start, so nothing about the order is known.
    /// </summary>
    [Fact]
    public async Task AMarkerWindowsWillNotDescribeLeavesEveryBuildAlone()
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        var marker = Path.Combine(root, "app-2.0.0", SquirrelDiscovery.UnfinishedMarkerName);
        File.WriteAllText(marker, string.Empty);

        var provider = CreateProvider();
        CleanupPlan plan;

        using (DeniedDirectory.WithUnreadableFile(marker))
        {
            plan = await provider.PlanAsync();
        }

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("would not say whether its last update finished", StringComparison.Ordinal));

        await AssertEveryBuildSurvives(
            provider, plan, Path.Combine(root, "app-1.5.0"), Path.Combine(root, "app-2.0.0"));
    }

    /// <summary>
    /// The narrower window: the new build finished unpacking, and the update stopped before the index
    /// was rewritten. The stub starts the new build, but <c>Update.exe --processStart</c> starts the
    /// newest one the index names that is on disk — so the older build is the one those shortcuts
    /// start. An index that names nothing on disk leads those shortcuts nowhere, which is no answer
    /// either.
    /// </summary>
    [Theory]
    [InlineData("Chatterbox-1.5.0-full.nupkg")]
    [InlineData("Chatterbox-1.5.0-full.nupkg", "Chatterbox-2.1.0-full.nupkg")]
    [InlineData("Chatterbox-0.9.0-full.nupkg")]
    public async Task AnIndexThatDoesNotLeadToTheNewestBuildLeavesEveryBuildAlone(params string[] indexed)
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        WriteIndex(root, indexed);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not lead to the newest one", StringComparison.Ordinal));

        await AssertEveryBuildSurvives(
            provider, plan, Path.Combine(root, "app-1.5.0"), Path.Combine(root, "app-2.0.0"));
    }

    /// <summary>
    /// The control for the theory above, so it cannot pass by refusing every index: an index that
    /// leads to the newest build, by Squirrel's own rule, leaves the older build offered. That rule
    /// skips an entry with no folder, and falls back to the folder named for the first three
    /// components of a version, which is how the updater reaches builds an older Squirrel installed.
    /// </summary>
    [Theory]
    [InlineData("Chatterbox-2.0.0-full.nupkg")]
    [InlineData("Chatterbox-1.5.0-full.nupkg", "Chatterbox-2.0.0-delta.nupkg", "Chatterbox-2.0.0-full.nupkg")]
    [InlineData("Chatterbox-2.0.0-full.nupkg", "Chatterbox-2.1.0-full.nupkg")]
    [InlineData("Chatterbox-2.0-full.nupkg")]
    public async Task AnIndexThatLeadsToTheNewestBuildLeavesTheOlderOneOffered(params string[] indexed)
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        var newest = Path.Combine(root, "app-2.0.0");
        WriteIndex(root, indexed);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(root, "app-1.5.0"), Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == newest && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(newest), $"{newest} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A pre-release in the index. Squirrel orders it by a rule Deguffer does not reproduce, so even
    /// an index that would otherwise lead to the newest build is not taken as an answer — and the
    /// sentence says that, rather than suggesting an update stopped.
    /// </summary>
    [Fact]
    public async Task AnIndexNamingAVersionItCannotOrderLeavesEveryBuildAlone()
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        WriteIndex(root, "Chatterbox-2.0.0-full.nupkg", "Chatterbox-2.1.0-beta1-full.nupkg");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("names a version Deguffer cannot order", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("may not have finished", StringComparison.Ordinal));

        await AssertEveryBuildSurvives(
            provider, plan, Path.Combine(root, "app-1.5.0"), Path.Combine(root, "app-2.0.0"));
    }

    /// <summary>
    /// Two builds named alike but for case, which only a case-sensitive folder can hold. Which of them
    /// either launch path reaches by name is not knowable, so the installation has no current build —
    /// and asking the index about it must not throw out of the planning pass. Built directly, because
    /// a test must not change a folder's case sensitivity on the machine running it.
    /// </summary>
    [Fact]
    public void BuildsNamedAlikeButForCaseHaveNoCurrentBuild()
    {
        var root = Path.Combine(_environment.LocalAppData, "Chatterbox");

        SquirrelVersionDirectory Build(string name, string version) =>
            new(Path.Combine(root, name), name, Version.Parse(version), IsLink: false, PathPresence.Absent);

        var installation = new SquirrelInstallation(
            "Chatterbox",
            root,
            [Build("app-1.5.0", "1.5.0"), Build("APP-2.0.0", "2.0.0"), Build("app-2.0.0", "2.0.0")],
            [],
            new SquirrelReleaseIndex(
                SquirrelIndexState.Read,
                new HashSet<string>(["Chatterbox-2.0.0-full.nupkg"], StringComparer.OrdinalIgnoreCase)));

        Assert.Equal(SquirrelOrderDoubt.AmbiguousBuilds, installation.Doubt);
        Assert.Null(installation.Current);
        Assert.Empty(installation.Superseded);
    }

    /// <summary>
    /// An application holding one build has nothing to offer whatever state it is in, so a doubt
    /// about it is not news: the row reads as examined, with the one-build sentence, rather than
    /// claiming something was left alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADoubtAboutAnApplicationHoldingOneBuildWithholdsNothing(bool marker)
    {
        var root = CreateApplication("Chatterbox", "2.0.0");

        if (marker)
        {
            File.WriteAllText(Path.Combine(root, "app-2.0.0", SquirrelDiscovery.UnfinishedMarkerName), string.Empty);
        }
        else
        {
            WriteIndex(root, "Chatterbox-0.9.0-full.nupkg");
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Left every version", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains("one build and no more", StringComparison.Ordinal));
    }

    /// <summary>
    /// An index that is there and cannot be read, or that Windows would not describe, could name the
    /// older build, so the order is unknown. Each says which of the two it was, and names the file.
    /// </summary>
    [Theory]
    [InlineData(false, "Deguffer could not read Chatterbox's record of which build to start at '")]
    [InlineData(true, "Windows would not say whether Chatterbox's record of which build to start is at '")]
    public async Task AnIndexThatCannotBeReadLeavesEveryBuildAlone(bool unreached, string sentence)
    {
        var root = CreateApplication("Chatterbox", "1.5.0", "2.0.0");
        var index = WriteIndex(root, "Chatterbox-2.0.0-full.nupkg");

        if (!unreached)
        {
            File.WriteAllText(index, "this is not a release index");
        }

        var provider = CreateProvider();
        CleanupPlan plan;

        using (unreached ? DeniedDirectory.WithUnreadableFile(index) : null)
        {
            plan = await provider.PlanAsync();
        }

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith(sentence + index + "'", StringComparison.Ordinal));

        await AssertEveryBuildSurvives(
            provider, plan, Path.Combine(root, "app-1.5.0"), Path.Combine(root, "app-2.0.0"));
    }
}
