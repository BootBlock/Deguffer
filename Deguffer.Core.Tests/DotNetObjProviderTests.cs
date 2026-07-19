using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider with no fixed location, so the questions here are different from every other
/// provider's: what is inside an approved root, what identity a candidate can prove, and what
/// happens when the fast route to finding them is unavailable.
/// </summary>
public sealed class DotNetObjProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public DotNetObjProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private string ApproveRoot(string name = "src")
    {
        var root = _temp.CreateDirectory(name);
        _roots.Save([root]);
        return root;
    }

    private DotNetObjProvider CreateProvider(
        IDirectoryScanner? scanner = null,
        FakeProcessRunner? runner = null) =>
        new(_roots,
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner ?? new FakeDirectoryScanner());

    /// <summary>
    /// A machine with no .NET and no approved folders has nothing to say, so the row is not shown
    /// at all rather than shown empty.
    /// </summary>
    [Fact]
    public async Task ReportsNotPresentWithoutTheSdkOrAnApprovedFolder()
    {
        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The guidance has to be reachable. A developer with the SDK installed and no folders approved
    /// is exactly who needs telling that approving one is what makes this work — and if presence
    /// were keyed on approved folders alone, the provider would be invisible to them and the
    /// sentence below could never appear in the window.
    /// </summary>
    [Fact]
    public async Task TellsAnSdkUserWithNoApprovedFolderHowToConfigureIt()
    {
        _environment.WithExecutable("dotnet");
        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Settings", StringComparison.Ordinal));
    }

    /// <summary>
    /// Approved folders keep the provider present on their own: uninstalling the SDK should not
    /// silently orphan folders the user deliberately chose.
    /// </summary>
    [Fact]
    public async Task StaysPresentOnApprovedFoldersAloneWithoutTheSdk()
    {
        ApproveRoot();

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansARecognisedProjectWithItsMeasuredSize()
    {
        var root = ApproveRoot();
        var obj = ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([obj], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Contains(plan.Steps.OfType<DeleteDirectoryStep>(), s =>
            s.What.Contains("Example.csproj", StringComparison.Ordinal));
    }

    /// <summary>
    /// The consent model. The index knows every directory on the volume, and a cheap answer must
    /// not become permission — an <c>obj</c> outside every approved root is never offered, however
    /// plainly it is intermediate output.
    /// </summary>
    [Fact]
    public async Task NeverOffersAnObjFoundOutsideEveryApprovedRoot()
    {
        var root = ApproveRoot();
        var inside = ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");
        var outside = ProjectFixture.CreateProject(
            _temp.CreateDirectory("elsewhere", "Unapproved"), "Unapproved");

        // The index offers both; narrowing to the approved root is what excludes the second.
        var plan = await CreateProvider(new FakeDirectoryScanner([inside, outside])).PlanAsync();

        Assert.Equal([inside], plan.TargetedPaths);
        Assert.DoesNotContain(outside, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(LongPath.DirectoryExists(outside));
    }

    /// <summary>
    /// §5.5's fallback must be observable. With no index, discovery still finds the project by
    /// walking the approved root — and the plan says that is what happened, so the user is told
    /// elevating would make it quick rather than left with an unexplained pause.
    /// </summary>
    [Fact]
    public async Task FindsCandidatesByWalkingWhenTheIndexIsUnavailableAndSaysSo()
    {
        var root = ApproveRoot();
        var obj = ProjectFixture.CreateProject(Path.Combine(root, "nested", "Example"), "Example");

        var scanner = new FakeDirectoryScanner(indexed: null);
        var plan = await CreateProvider(scanner).PlanAsync();

        Assert.Equal(1, scanner.FindCalls);
        Assert.Equal([obj], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("administrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesTheIndexWithoutWalkingWhenItIsAvailable()
    {
        var root = ApproveRoot();
        var obj = ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");

        var plan = await CreateProvider(new FakeDirectoryScanner([obj])).PlanAsync();

        Assert.Equal([obj], plan.TargetedPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("administrator", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2's dangerous direction, end to end: the art-asset directory is inside an approved root
    /// and named <c>obj</c>, and the provider must decline it rather than treat an unknown thing as
    /// safe. It is also carried as a protected path, so execution verifies it survived.
    /// </summary>
    [Fact]
    public async Task LeavesArtAssetsAloneAndProtectsThem()
    {
        var root = ApproveRoot();
        ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");
        var art = ProjectFixture.CreateArtAssets(Path.Combine(root, "game", "addons", "Assets"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(art, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(art, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not be confirmed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declined count is a sentence the user reads, and it is the only place they are told
    /// something was passed over. Driving the real window turned up "Left 1 directory … because
    /// they could not be confirmed", so both forms are pinned rather than only the plural the
    /// developer happens to see on their own machine.
    /// </summary>
    [Theory]
    [InlineData(1, "Left 1 directory named 'obj' alone because it could not be confirmed")]
    [InlineData(3, "Left 3 directories named 'obj' alone because they could not be confirmed")]
    public async Task CountsTheDirectoriesItDeclinedWithAgreeingGrammar(int count, string expected)
    {
        var root = ApproveRoot();
        ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");

        for (var i = 0; i < count; i++)
        {
            ProjectFixture.CreateArtAssets(Path.Combine(root, "game", $"pack{i}"));
        }

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.StartsWith(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6. The three ways an over-broad rule could reach past the directory it recognised, each
    /// asserted as a protected path rather than merely happening to survive.
    /// </summary>
    [Fact]
    public async Task ProtectsTheProjectDirectoryTheProjectFileAndBin()
    {
        var root = ApproveRoot();
        var directory = Path.Combine(root, "Example");
        ProjectFixture.CreateProject(directory, "Example");
        Directory.CreateDirectory(Path.Combine(directory, "bin"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(directory, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(directory, "Example.csproj"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(directory, "bin"), StringComparison.OrdinalIgnoreCase));

        Assert.Empty(plan.ProtectedPaths.Select(p => p.Path)
            .Intersect(plan.TargetedPaths, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The §5.6 negative after a real execution: the output goes, and everything around it is still
    /// standing — including the art assets, which is the failure this whole rule exists to prevent.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheOutputAndLeavesTheProjectAndArtAssetsStanding()
    {
        var root = ApproveRoot();
        var directory = Path.Combine(root, "Example");
        var obj = ProjectFixture.CreateProject(directory, "Example");
        var bin = Path.Combine(directory, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "native.dll"), new byte[2048]);

        var art = ProjectFixture.CreateArtAssets(Path.Combine(root, "game", "Assets"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(obj));

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "Example.csproj")));
        Assert.True(File.Exists(Path.Combine(bin, "native.dll")));
        Assert.True(Directory.Exists(art));
        Assert.True(File.Exists(Path.Combine(art, "barrel.obj")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7's cross-check. A tracked file inside an <c>obj</c> means it is committed content whatever
    /// the manifest beside it claims, so recognition is overruled and the directory is protected.
    /// </summary>
    [Fact]
    public async Task LeavesARecognisedObjAloneWhenGitReportsItHoldsTrackedFiles()
    {
        var root = ApproveRoot();
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));

        var obj = ProjectFixture.CreateProject(Path.Combine(repository, "Example"), "Example");

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner().Responding("ls-files", "Example/obj/committed.props\0");

        var plan = await CreateProvider(runner: runner).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(obj, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("tracked in git", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cross-check costs one process per repository, not per directory. Three projects in one
    /// repository is one invocation — a per-directory check would cost more than the walk it guards.
    /// </summary>
    [Fact]
    public async Task AsksGitOncePerRepositoryRatherThanOncePerDirectory()
    {
        var root = ApproveRoot();
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));

        foreach (var name in new[] { "One", "Two", "Three" })
        {
            ProjectFixture.CreateProject(Path.Combine(repository, name), name);
        }

        _environment.WithExecutable("git");
        var runner = new FakeProcessRunner();

        var plan = await CreateProvider(runner: runner).PlanAsync();

        Assert.Equal(3, plan.TargetedPaths.Count);
        Assert.Single(runner.Invocations);
    }

    /// <summary>
    /// §7's age column: one row per project, so a project untouched for a year is distinguishable
    /// from this morning's.
    /// </summary>
    [Fact]
    public async Task DatesEachProjectByItsMostRecentBuildOutput()
    {
        var root = ApproveRoot();
        var obj = ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");

        var stale = DateTime.UtcNow.AddDays(-400);
        foreach (var entry in new DirectoryInfo(obj).EnumerateFileSystemInfos())
        {
            entry.LastWriteTimeUtc = stale;
        }

        var plan = await CreateProvider().PlanAsync();
        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());

        Assert.NotNull(step.LastWritten);
        Assert.Equal(stale, step.LastWritten!.Value, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// §6.3. Intermediate output nests deeply by construction — <c>obj/Debug/&lt;tfm&gt;/</c> under
    /// an already-deep source tree — so a MAX_PATH truncation here is an ordinary case, not an edge
    /// one, and it would be a silent partial deletion.
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesOutputNestedPastMaxPath()
    {
        var root = ApproveRoot();

        var deep = root;
        while (deep.Length < 250)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var directory = Path.Combine(deep, "Example");
        var obj = ProjectFixture.CreateProject(directory, "Example", payloadBytes: 8192);

        // The payload is what has to be reached to be measured and removed, so it is the path that
        // has to be past the limit.
        var payload = Path.Combine(obj, "Debug", "net10.0", "Example.dll");
        Assert.True(payload.Length > 260, $"Path was only {payload.Length} characters.");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([obj], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes >= 8192, $"Deep content was not measured: {plan.EstimatedBytes} bytes.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(obj));
        Assert.True(LongPath.FileExists(Path.Combine(directory, "Example.csproj")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6 under per-item selection: deselecting one project must protect it, because after
    /// narrowing the kept and the dropped are directories of identical shape in the same tree.
    /// </summary>
    [Fact]
    public async Task NarrowingToOneProjectSparesTheOtherOnDisk()
    {
        var root = ApproveRoot();
        var chosen = ProjectFixture.CreateProject(Path.Combine(root, "Chosen"), "Chosen");
        var deselected = ProjectFixture.CreateProject(Path.Combine(root, "Deselected"), "Deselected");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var keep = plan.Steps.OfType<DeleteDirectoryStep>()
            .Where(s => s.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = await provider.ExecuteAsync(plan.NarrowedTo(keep));

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(chosen));
        Assert.True(Directory.Exists(deselected));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A root the user approved that is not currently attached finds nothing, rather than failing
    /// the pass or dropping the approval.
    /// </summary>
    [Fact]
    public async Task TreatsAMissingApprovedRootAsFindingNothing()
    {
        var root = ApproveRoot();
        ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");
        _roots.Save([root, Path.Combine(_temp.Path, "not-attached")]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.TargetedPaths);
    }

    [Fact]
    public async Task RereadsApprovedRootsAfterInvalidationSoASettingsChangeTakesEffect()
    {
        var provider = CreateProvider();
        Assert.False(await provider.IsPresentAsync());

        var root = ApproveRoot();
        ProjectFixture.CreateProject(Path.Combine(root, "Example"), "Example");

        provider.InvalidateCaches();

        Assert.True(await provider.IsPresentAsync());
        Assert.Single((await provider.PlanAsync()).TargetedPaths);
    }
}
