using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The four providers that remove a whole build directory out of a user's own source folder.
///
/// What they have in common is where the risk is. Every other provider deletes inside a directory a
/// toolchain owns; these delete inside the developer's project, one folder away from work that
/// exists nowhere else. So the questions are always the same three: does the evidence actually prove
/// what the directory is, does the source beside it survive, and is anybody using it right now.
///
/// Everything here runs through the fakes and a scratch tree. No Unity, Cargo, Node or Python is
/// installed for any of it, which is the point — a rule that could only be proved on a machine with
/// the toolchain would not be proving the safety rule at all.
/// </summary>
public sealed class BuildDirectoryProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public BuildDirectoryProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    // ---- recognition -------------------------------------------------------------------------

    [Fact]
    public async Task PlansAUnityLibraryWithItsMeasuredSize()
    {
        var root = ApproveRoot();
        var library = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));

        var plan = await PlanWith(Unity);

        Assert.Equal([library], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    [Fact]
    public async Task PlansACargoTargetWithItsMeasuredSize()
    {
        var root = ApproveRoot();
        var target = BuildDirectoryFixture.CreateCargoProject(Path.Combine(root, "crate"));

        var plan = await PlanWith(Cargo);

        Assert.Equal([target], plan.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    [Fact]
    public async Task PlansANodeModulesWithItsMeasuredSize()
    {
        var root = ApproveRoot();
        var modules = BuildDirectoryFixture.CreateNodeProject(Path.Combine(root, "app"));

        Assert.Equal([modules], (await PlanWith(Node)).TargetedPaths);
    }

    [Theory]
    [InlineData(".venv")]
    [InlineData("venv")]
    public async Task PlansAPythonEnvironmentUnderEitherOfItsUsualNames(string name)
    {
        var root = ApproveRoot();
        var environment = BuildDirectoryFixture.CreatePythonProject(
            Path.Combine(root, "tool"), directoryName: name);

        var plan = await PlanWith(Python);
        Assert.Equal([environment], plan.TargetedPaths);
    }

    // ---- §5.2: unrecognised is left alone ----------------------------------------------------

    /// <summary>
    /// §5.2's dangerous direction is treating an unknown thing as safe, and outside a tool's own
    /// root a directory's name is the whole of what a careless rule would go on. Each case here is a
    /// directory of exactly the right name with one piece of evidence missing, which is what a real
    /// mistake looks like — a photographer's <c>Library</c>, a hand-made <c>target</c>, a
    /// <c>node_modules</c> in a project with no lock file to restore it from.
    /// </summary>
    [Fact]
    public async Task ADirectoryOfTheRightNameWithoutTheEvidenceIsNeverATarget()
    {
        var root = ApproveRoot();

        var noAssets = BuildDirectoryFixture.CreateUnityProject(
            Path.Combine(root, "photos"), writeAssets: false);
        var noPackages = BuildDirectoryFixture.CreateUnityProject(
            Path.Combine(root, "music"), writePackages: false);
        var noSettings = BuildDirectoryFixture.CreateUnityProject(
            Path.Combine(root, "samples"), writeProjectSettings: false);

        var plan = await PlanWith(Unity);

        Assert.Empty(plan.TargetedPaths);
        Assert.True(LongPath.DirectoryExists(noAssets));
        Assert.True(LongPath.DirectoryExists(noPackages));
        Assert.True(LongPath.DirectoryExists(noSettings));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be confirmed", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == noAssets);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == noPackages);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == noSettings);
    }

    /// <summary>
    /// Cargo's manifest alone is not enough. <c>CACHEDIR.TAG</c> is written by Cargo itself, so it
    /// is the part that says this directory is Cargo's rather than merely that a Rust project is
    /// nearby — a hand-made <c>target</c> beside a <c>Cargo.toml</c> is somebody's own folder.
    /// </summary>
    [Fact]
    public async Task ACargoTargetWithoutTheToolsOwnMarkerIsNotRecognised()
    {
        var root = ApproveRoot();
        var target = BuildDirectoryFixture.CreateCargoProject(
            Path.Combine(root, "crate"), writeCacheTag: false);

        var plan = await PlanWith(Cargo);

        Assert.Empty(plan.TargetedPaths);
        Assert.True(LongPath.DirectoryExists(target));
    }

    /// <summary>
    /// The other direction: the tool's own marker without the manifest beside it. Either half alone
    /// is a coincidence, and it is the conjunction that identifies the directory.
    /// </summary>
    [Fact]
    public async Task ABuildDirectoryWithNoProjectManifestBesideItIsNotRecognised()
    {
        var root = ApproveRoot();
        var target = BuildDirectoryFixture.CreateCargoProject(
            Path.Combine(root, "crate"), writeManifest: false);
        var modules = BuildDirectoryFixture.CreateNodeProject(
            Path.Combine(root, "app"), writeManifest: false);

        Assert.Empty((await PlanWith(Cargo)).TargetedPaths);
        Assert.Empty((await PlanWith(Node)).TargetedPaths);
        Assert.True(LongPath.DirectoryExists(target));
        Assert.True(LongPath.DirectoryExists(modules));
    }

    /// <summary>
    /// Without a lock file the dependency tree is not reproducible, so "regenerable" is not true of
    /// it and Tier 2 would be a false claim. Leaving the space unreclaimed is the safe direction.
    /// </summary>
    [Fact]
    public async Task ANodeModulesWithNoLockFileIsNotRecognised()
    {
        var root = ApproveRoot();
        var modules = BuildDirectoryFixture.CreateNodeProject(Path.Combine(root, "app"), lockFile: null);

        Assert.Empty((await PlanWith(Node)).TargetedPaths);
        Assert.True(LongPath.DirectoryExists(modules));
    }

    /// <summary>
    /// A virtual environment with no manifest beside it is the only copy of what was installed into
    /// it. Removing that destroys information rather than freeing space, which is Tier 3's own
    /// definition arriving inside a Tier 2 subject.
    /// </summary>
    [Fact]
    public async Task APythonEnvironmentWithNoManifestIsNotRecognised()
    {
        var root = ApproveRoot();
        var environment = BuildDirectoryFixture.CreatePythonProject(
            Path.Combine(root, "scratch"), manifest: null);

        Assert.Empty((await PlanWith(Python)).TargetedPaths);
        Assert.True(LongPath.DirectoryExists(environment));
    }

    /// <summary>
    /// A directory called <c>venv</c> that is not one. <c>pyvenv.cfg</c> is what PEP 405 makes an
    /// environment, and the manifest beside it proves nothing about the folder itself.
    /// </summary>
    [Fact]
    public async Task ADirectoryCalledVenvThatIsNotOneIsNotRecognised()
    {
        var root = ApproveRoot();
        var notAnEnvironment = BuildDirectoryFixture.CreatePythonProject(
            Path.Combine(root, "scratch"), writeConfig: false);

        Assert.Empty((await PlanWith(Python)).TargetedPaths);
        Assert.True(LongPath.DirectoryExists(notAnEnvironment));
    }

    // ---- §5.6: the negative --------------------------------------------------------------------

    /// <summary>
    /// §5.6, and here the protected path is the user's own source. Asserting the build directory
    /// went is half a test: the half that matters is that the project folder and every file the
    /// regeneration reads are still standing, because those are what an over-broad rule would take
    /// and what no rebuild would bring back.
    /// </summary>
    [Fact]
    public async Task TheProjectAndItsSourceAreAssertedToSurviveAndDo()
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "Game");
        var library = BuildDirectoryFixture.CreateUnityProject(project);
        var source = Path.Combine(project, "Assets", "Player.cs");

        var provider = Unity();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path == project && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "Assets"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "ProjectSettings"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(library));

        // The negative, checked against the disk rather than only against the report.
        Assert.True(LongPath.DirectoryExists(project));
        Assert.True(LongPath.FileExists(source));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The same negative for a project whose source sits directly beside the target. Cargo's
    /// <c>src</c> and its manifest are one directory level away from a five-gigabyte deletion.
    /// </summary>
    [Fact]
    public async Task ACargoProjectsSourceSurvivesTheDeletion()
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "crate");
        var target = BuildDirectoryFixture.CreateCargoProject(project);

        var provider = Cargo();
        var plan = await provider.PlanAsync();

        // Named as survivors in the plan, not merely left standing by luck. Neither identifies the
        // directory being removed, so neither would be here if §5.6's question were "what did
        // recognition read" rather than "what would an over-broad rule take".
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "src") && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(project, "Cargo.lock") && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(target));
        Assert.True(LongPath.FileExists(Path.Combine(project, "Cargo.toml")));
        Assert.True(LongPath.FileExists(Path.Combine(project, "Cargo.lock")));
        Assert.True(LongPath.FileExists(Path.Combine(project, "src", "main.rs")));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    // ---- consent ------------------------------------------------------------------------------

    /// <summary>
    /// The consent model, and it is the same one <c>obj</c> has: the index knows every directory on
    /// the volume, and a cheap answer must not become permission. A Unity project outside every
    /// approved root is never offered, however plainly its <c>Library</c> is regenerable.
    /// </summary>
    [Fact]
    public async Task NeverOffersAProjectFoundOutsideEveryApprovedRoot()
    {
        var root = ApproveRoot();
        var inside = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));
        var outside = BuildDirectoryFixture.CreateUnityProject(
            _temp.CreateDirectory("elsewhere", "Other"));

        var plan = await PlanWith(Unity, new FakeDirectoryScanner([inside, outside]));

        Assert.Equal([inside], plan.TargetedPaths);
        Assert.True(LongPath.DirectoryExists(outside));
    }

    /// <summary>
    /// With nothing approved the provider is absent, not empty. A row that can never find anything
    /// is noise, and the guidance says what approving a folder would make it look for.
    /// </summary>
    [Fact]
    public async Task IsAbsentUntilAFolderIsApprovedAndSaysWhatItWouldLookFor()
    {
        var provider = Unity();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Settings", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Unity", StringComparison.Ordinal));
    }

    // ---- the live-tree veto --------------------------------------------------------------------

    /// <summary>
    /// A directory something is using is never a target. It is not a warning beside one: a build
    /// directory removed under a live editor breaks the work in progress, so the step does not exist
    /// at all, the path is asserted to survive, and the plan says why it is missing.
    /// </summary>
    [Fact]
    public async Task ALiveProjectIsNotATargetAtAll()
    {
        var root = ApproveRoot();
        var busy = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Open"));
        var idle = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Dormant"));

        var provider = Unity(live: new FakeLiveTreeInspector(busy));
        var plan = await provider.PlanAsync();

        Assert.Equal([idle], plan.TargetedPaths);
        Assert.DoesNotContain(busy, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == busy);

        // Named, and named singly: the note has to say which project and what is holding it, or the
        // user cannot act on it. Both grammatical forms are covered, because one project held back
        // is the ordinary case and the sentence reads wrong on a developer's machine otherwise.
        var note = Assert.Single(plan.Notes, n => n.Message.StartsWith("Left Library in Open", StringComparison.Ordinal));

        Assert.Contains("Close what is using it and preview again", note.Message, StringComparison.Ordinal);

        // §5.6's negative, on the case it exists for: two directories of the same name in the same
        // tree, separated only by evidence. Asserting the target went proves nothing about the one
        // beside it, and an over-broad rule takes both.
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.False(LongPath.DirectoryExists(idle));
        Assert.True(LongPath.DirectoryExists(busy));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// Two projects held back are named separately, each with what is holding it. One project's
    /// holders attributed to all of them would be a wrong answer on the one sentence the user acts
    /// on to decide what to close.
    /// </summary>
    [Fact]
    public async Task EachHeldBackProjectIsNamedWithWhatIsHoldingIt()
    {
        var root = ApproveRoot();
        var first = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Open"));
        var second = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "AlsoOpen"));

        var plan = await PlanWith(Unity, liveTrees: new FakeLiveTreeInspector(first, second));

        Assert.Empty(plan.TargetedPaths);

        var note = Assert.Single(plan.Notes, n => n.Message.StartsWith("Left Library in", StringComparison.Ordinal));

        Assert.Contains("Library in Open", note.Message, StringComparison.Ordinal);
        Assert.Contains("Library in AlsoOpen", note.Message, StringComparison.Ordinal);
        Assert.Contains("Close what is using each one and preview again", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same negative for a directory held back because its evidence did not hold, rather than
    /// because something is using it. Both land in the same protected list, and both sit beside a
    /// directory that is about to be removed.
    /// </summary>
    [Fact]
    public async Task ADeclinedDirectoryBesideARemovedOneSurvivesTheRemoval()
    {
        var root = ApproveRoot();
        var target = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));
        var declined = BuildDirectoryFixture.CreateUnityProject(
            Path.Combine(root, "photos"), writePackages: false);

        var provider = Unity();
        var plan = await provider.PlanAsync();

        Assert.Equal([target], plan.TargetedPaths);
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        Assert.False(LongPath.DirectoryExists(target));
        Assert.True(LongPath.DirectoryExists(declined));
        Assert.True((await provider.VerifyAsync(plan)).Passed);
    }

    /// <summary>
    /// The provider asks about the project folder, not only the build directory. The strongest
    /// signal sits beside the target rather than inside it — a build, a shell and an open editor all
    /// work in the project — so passing the wrong path would make the veto answer for nothing.
    /// </summary>
    [Fact]
    public async Task TheVetoIsAskedAboutTheProjectFolderAndTheToolsOwnLockFile()
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "Game");
        var library = BuildDirectoryFixture.CreateUnityProject(project);

        var inspector = FakeLiveTreeInspector.NothingLive;
        await PlanWith(Unity, liveTrees: inspector);

        var asked = Assert.Single(inspector.Asked);

        Assert.Equal(library, asked.Directory);
        Assert.Equal(project, asked.Project);
        Assert.Equal(["UnityLockfile"], asked.LockFileNames);
    }

    /// <summary>
    /// "Nothing is using this" and "we could not tell" are different answers, and only the first is
    /// permission. A check that could not run says so, for the same reason §5.5 makes the
    /// measurement fallback observable — a safeguard that could not run must not look like one that
    /// found nothing.
    /// </summary>
    [Fact]
    public async Task AVetoThatCouldNotRunIsSaidOutLoud()
    {
        var root = ApproveRoot();
        BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));

        var plan = await PlanWith(Unity, liveTrees: FakeLiveTreeInspector.CannotTell);

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not check whether these projects are in use", StringComparison.Ordinal));
    }

    // ---- §6.3, §7 and discovery ----------------------------------------------------------------

    /// <summary>
    /// A Cargo project buried four long segments below an approved root is still discovered,
    /// measured and removed, with its sources left standing. That is worth covering, because
    /// discovery here walks a tree the user nominated rather than a location the provider knows.
    ///
    /// It is not the §6.3 proof its shape suggests. .NET prepends <c>\\?\</c> itself to any path of
    /// 260 characters or more before it calls Win32, so this passes with Core's prefixing deleted
    /// outright — see <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>.
    /// What discriminates is the form of the path, asserted in
    /// <see cref="DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/>.
    /// </summary>
    [Fact]
    public async Task FindsAndRemovesAProjectPastMaxPath()
    {
        var root = ApproveRoot();
        var deep = Path.Combine(root, string.Join('\\', Enumerable.Repeat(new string('p', 60), 4)));
        var target = BuildDirectoryFixture.CreateCargoProject(deep);

        Assert.True(target.Length > 260, "the fixture is not long enough to test anything");

        var provider = Cargo();
        var plan = await provider.PlanAsync();

        Assert.Equal([target], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.False(LongPath.DirectoryExists(target));
        Assert.True(LongPath.FileExists(Path.Combine(deep, "src", "main.rs")));
    }

    /// <summary>
    /// §7's age column. A build directory's own timestamp moves only when its top-level entries
    /// change, so the age is read from the newest entry inside it — and a step with no age would
    /// render as "Unknown", which is the one thing that must not happen to a project the user is
    /// being asked to judge by how long ago they touched it.
    /// </summary>
    [Fact]
    public async Task EveryStepCarriesAnAgeForSection7sColumn()
    {
        var root = ApproveRoot();
        BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));

        var step = Assert.Single((await PlanWith(Unity)).Steps);

        Assert.NotNull(step.LastWritten);
        Assert.NotEqual("Unknown", RelativeAge.Describe(step.LastWritten, DateTime.UtcNow));
    }

    /// <summary>
    /// <c>node_modules</c> is the directory <see cref="SourceDirectoryDiscovery"/> otherwise refuses
    /// to walk into, and the two rules have to agree: a search stops at a name it is looking for, so
    /// the top-level one is found without the tree beneath it being enumerated, and one nested
    /// inside another belongs to its parent rather than being offered as a second step.
    /// </summary>
    [Fact]
    public async Task ANestedNodeModulesBelongsToItsParentAndIsNotOfferedSeparately()
    {
        var root = ApproveRoot();
        var outer = BuildDirectoryFixture.CreateNodeProject(Path.Combine(root, "app"));
        BuildDirectoryFixture.CreateNodeProject(Path.Combine(outer, "nested-package"));

        Assert.Equal([outer], (await PlanWith(Node)).TargetedPaths);
    }

    /// <summary>
    /// The same rule for a name the boundary knows nothing about. <c>node_modules</c> is protected
    /// twice over — the search stops at it and would refuse to enter it anyway — so it cannot show
    /// whether the stop is doing any work. A vendored crate inside a <c>target</c> can: nothing but
    /// "everything below a candidate belongs to that candidate" keeps the inner one out of the plan,
    /// and offering both would make two steps where one deletes the other's parent.
    /// </summary>
    [Fact]
    public async Task ACandidateNestedInsideAnotherOfTheSameKindIsNotOfferedSeparately()
    {
        var root = ApproveRoot();
        var outer = BuildDirectoryFixture.CreateCargoProject(Path.Combine(root, "crate"));
        var inner = BuildDirectoryFixture.CreateCargoProject(Path.Combine(outer, "vendored"));

        Assert.True(LongPath.DirectoryExists(inner), "the fixture did not build the nested case");
        Assert.Equal([outer], (await PlanWith(Cargo)).TargetedPaths);
    }

    /// <summary>
    /// The same agreement on the indexed route. The volume index has no traversal to stop, so it
    /// applies the rule as a filter — and if the two disagreed, whether a directory was offered
    /// would depend on whether the user happened to run Deguffer as administrator.
    /// </summary>
    [Fact]
    public async Task TheIndexedRouteAgreesAboutACandidateNestedInsideAnother()
    {
        var root = ApproveRoot();
        var outer = BuildDirectoryFixture.CreateCargoProject(Path.Combine(root, "crate"));
        var inner = BuildDirectoryFixture.CreateCargoProject(Path.Combine(outer, "vendored"));

        var plan = await PlanWith(Cargo, new FakeDirectoryScanner([outer, inner]));

        Assert.Equal([outer], plan.TargetedPaths);
    }

    /// <summary>
    /// The indexed route's answer for the pair above, where the boundary would have covered it
    /// anyway. Kept because <c>node_modules</c> is the collision this phase had to resolve: the
    /// search now stops at a directory it also refuses to enter, and the two must not disagree.
    /// </summary>
    [Fact]
    public async Task TheIndexedRouteAgreesAboutANestedNodeModules()
    {
        var root = ApproveRoot();
        var outer = BuildDirectoryFixture.CreateNodeProject(Path.Combine(root, "app"));
        var nested = BuildDirectoryFixture.CreateNodeProject(Path.Combine(outer, "nested-package"));

        var plan = await PlanWith(Node, new FakeDirectoryScanner([outer, nested]));

        Assert.Equal([outer], plan.TargetedPaths);
    }

    /// <summary>
    /// A junction is refused. A recognised marker behind a link would let a deletion leave the
    /// directory that was examined and land in whatever the link points at.
    /// </summary>
    [Fact]
    public void AJunctionedBuildDirectoryIsNotRecognised()
    {
        var project = _temp.CreateDirectory("crate");
        var real = BuildDirectoryFixture.CreateCargoProject(_temp.CreateDirectory("real"));
        var link = Path.Combine(project, "target");

        File.WriteAllText(Path.Combine(project, "Cargo.toml"), "[package]");
        Directory.CreateSymbolicLink(link, real);

        Assert.Null(BuildDirectorySignature.TryRecognise(
            new BuildDirectoryKind
            {
                DirectoryNames = ["target"],
                RequiredSiblings = ["Cargo.toml"],
                RequiredContents = ["CACHEDIR.TAG"],
            },
            link));
    }

    /// <summary>
    /// The walk is the only route that can meet a refusal: the index reads the volume table rather
    /// than descending, so a directory it may not list costs it nothing. A directory the walk cannot
    /// descend into leaves part of a root the user approved unsearched, and every other sentence in
    /// the plan describes a sweep of the whole root.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheWalkCouldNotDescendIntoIsNamedRatherThanPassedOverInSilence()
    {
        var root = ApproveRoot();
        var library = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Game"));
        var refused = Path.Combine(root, "Archive");
        Directory.CreateDirectory(refused);

        using var denied = new DeniedDirectory(refused);

        // A FakeDirectoryScanner with no index answers null, which is the contract that sends
        // discovery down the walk — so the Library was found by descending, not from a table.
        var plan = await PlanWith(Unity, new FakeDirectoryScanner());

        Assert.Equal([library], plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(refused));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private string ApproveRoot(string name = "src")
    {
        var root = _temp.CreateDirectory(name);
        _roots.Save([root]);
        return root;
    }

    private Task<CleanupPlan> PlanWith(
        Func<IDirectoryScanner?, ILiveTreeInspector?, BuildDirectoryProvider> create,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null) =>
        create(scanner, liveTrees).PlanAsync();

    private BuildDirectoryProvider Unity(IDirectoryScanner? scanner = null, ILiveTreeInspector? live = null) =>
        new UnityLibraryProvider(
            _roots, null, live ?? FakeLiveTreeInspector.NothingLive, _environment, new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning, scanner ?? new FakeDirectoryScanner());

    private BuildDirectoryProvider Cargo(IDirectoryScanner? scanner = null, ILiveTreeInspector? live = null) =>
        new CargoTargetProvider(
            _roots, null, live ?? FakeLiveTreeInspector.NothingLive, _environment, new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning, scanner ?? new FakeDirectoryScanner());

    private BuildDirectoryProvider Node(IDirectoryScanner? scanner = null, ILiveTreeInspector? live = null) =>
        new NodeModulesProvider(
            _roots, null, live ?? FakeLiveTreeInspector.NothingLive, _environment, new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning, scanner ?? new FakeDirectoryScanner());

    private BuildDirectoryProvider Python(IDirectoryScanner? scanner = null, ILiveTreeInspector? live = null) =>
        new PythonVirtualEnvironmentProvider(
            _roots, null, live ?? FakeLiveTreeInspector.NothingLive, _environment, new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning, scanner ?? new FakeDirectoryScanner());

}
