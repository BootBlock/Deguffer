using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider whose targets are decided by reading another tool's own index, and the first
/// whose root is shared by every application of a kind rather than owned by one.
///
/// <para>Three things have to hold. The staging folder gives up only the directories Squirrel's own
/// name generator produced, and a directory something is installing through is refused rather than
/// warned about. The packages folder gives up only what the application's index has stopped naming,
/// never the index itself — a shortcut reads that file to decide which build to launch — and never
/// a package for a build newer than the one installed, which is a download in progress. And an
/// index that cannot be read leaves the whole folder alone.</para>
/// </summary>
public sealed class SquirrelStagingProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SquirrelStagingProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>Squirrel's shared staging folder, where it sits when nothing has moved it.</summary>
    private string StagingRoot =>
        Path.Combine(_environment.LocalAppData, SquirrelDiscovery.StagingDirectoryName);

    private SquirrelStagingProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
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
    /// An application Squirrel installed: the updater it puts beside every one, and a directory per
    /// build. Both halves are needed — the provider treats neither alone as an installation.
    /// </summary>
    private string CreateApplication(string name, params string[] versions)
    {
        var root = Path.Combine(_environment.LocalAppData, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);

        foreach (var version in versions)
        {
            Populate(Path.Combine(root, "app-" + version));
        }

        return root;
    }

    /// <summary>
    /// A packages folder holding <paramref name="files"/>, with an index naming
    /// <paramref name="indexed"/>. The index is written in Squirrel's own format, because a provider
    /// that only read a format nobody writes would find nothing on every real machine.
    /// </summary>
    private static string CreatePackages(string root, string[] indexed, params string[] files)
    {
        var packages = Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName);
        Directory.CreateDirectory(packages);

        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(packages, file), new byte[2048]);
        }

        File.WriteAllText(
            Path.Combine(packages, SquirrelPackages.IndexName),
            string.Join("\n", indexed.Select(f => $"{new string('A', 40)} {f} 2048")));

        return packages;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSquirrelApplication()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The staging folder, with the logs and the installer state Squirrel leaves beside its unpacked
    /// directories. Only the unpacked directories go, and the rest is asserted rather than omitted.
    /// </summary>
    [Fact]
    public async Task PlansTheUnpackedDirectoriesAndNothingElseInTheStagingFolder()
    {
        var first = Populate(Path.Combine(StagingRoot, "tempa"));
        var second = Populate(Path.Combine(StagingRoot, "tempb"));
        var unrecognised = Populate(Path.Combine(StagingRoot, "notes"));

        File.WriteAllText(Path.Combine(StagingRoot, "SquirrelSetup.log"), "log");
        File.WriteAllText(Path.Combine(StagingRoot, "setup.json"), "{}");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == unrecognised && p.ExistedBefore);
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe. The name Squirrel's generator
    /// produces is the prefix and at least one character from its own alphabet, so a bare
    /// <c>temp</c>, a numbered one and a differently cased one are all somebody else's directory.
    /// </summary>
    [Theory]
    [InlineData("temp")]
    [InlineData("temp1")]
    [InlineData("TEMPA")]
    [InlineData("tempa-old")]
    [InlineData("logs")]
    public async Task AnUnrecognisedNeighbourInTheStagingFolderIsNeverATarget(string name)
    {
        Populate(Path.Combine(StagingRoot, "tempz"));

        var neighbour = Populate(Path.Combine(StagingRoot, name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(neighbour, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(neighbour), $"{neighbour} was removed");
    }

    /// <summary>
    /// The packages folder against its own index: the one it still names stays, and the one it has
    /// stopped naming goes. This is the whole of what makes the folder safe to reach into.
    /// </summary>
    [Fact]
    public async Task PlansOnlyThePackagesTheApplicationsIndexNoLongerNames()
    {
        var root = CreateApplication("Chatterbox", "1.0.9254");
        var packages = CreatePackages(
            root,
            ["Chatterbox-1.0.9254-full.nupkg"],
            "Chatterbox-1.0.9254-full.nupkg",
            "Chatterbox-1.0.9007-full.nupkg");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            Path.Combine(packages, "Chatterbox-1.0.9007-full.nupkg"),
            Assert.Single(plan.TargetedPaths));
    }

    /// <summary>
    /// The <c>steamapps\downloading</c> trap, in Squirrel's own shape: an update is written into the
    /// packages folder <em>before</em> the index is rewritten, so a package the index does not name
    /// is not automatically debris. The version decides, and nothing newer than the installed build
    /// is ever offered.
    /// </summary>
    [Fact]
    public async Task APackageForABuildNewerThanTheInstalledOneIsLeftAlone()
    {
        var root = CreateApplication("Chatterbox", "1.0.9254");
        var packages = CreatePackages(
            root,
            ["Chatterbox-1.0.9254-full.nupkg"],
            "Chatterbox-1.0.9254-full.nupkg",
            "Chatterbox-1.0.9300-full.nupkg");

        var downloading = Path.Combine(packages, "Chatterbox-1.0.9300-full.nupkg");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == downloading && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(downloading), $"{downloading} was removed");
    }

    /// <summary>
    /// Without the index there is no way to tell a spent package from the one the next update is
    /// built against, so nothing in the folder is offered — and the row must not read "Already
    /// clear" about a folder Deguffer declined to classify.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("this is not a release index")]
    public async Task AnIndexThatCannotBeReadLeavesEveryPackageAlone(string? contents)
    {
        var root = CreateApplication("Chatterbox", "1.0.9254");
        var packages = Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName);
        Directory.CreateDirectory(packages);

        var spent = Path.Combine(packages, "Chatterbox-1.0.9007-full.nupkg");
        File.WriteAllBytes(spent, new byte[2048]);

        if (contents is not null)
        {
            File.WriteAllText(Path.Combine(packages, SquirrelPackages.IndexName), contents);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read the record", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(spent), $"{spent} was removed");
    }

    /// <summary>
    /// §5.6's negative, and the one that matters most here: the index a shortcut reads to decide
    /// which build to start, the identifier that places this machine in a staged rollout, the
    /// updater, the installed build and the package the next update is patched against all survive
    /// a run that removed a spent package and two staging directories.
    /// </summary>
    [Fact]
    public async Task TheIndexTheStagedIdentifierAndTheInstalledBuildAllSurvive()
    {
        Populate(Path.Combine(StagingRoot, "tempa"));
        Populate(Path.Combine(StagingRoot, "tempb"));

        var root = CreateApplication("Chatterbox", "1.0.9254");
        var packages = CreatePackages(
            root,
            ["Chatterbox-1.0.9254-full.nupkg"],
            "Chatterbox-1.0.9254-full.nupkg",
            "Chatterbox-1.0.9007-full.nupkg");

        File.WriteAllText(Path.Combine(packages, ".betaId"), Guid.NewGuid().ToString());

        string[] directories =
        [
            StagingRoot,
            root,
            packages,
            Path.Combine(root, "app-1.0.9254"),
        ];

        string[] files =
        [
            Path.Combine(root, SquirrelDiscovery.UpdaterName),
            Path.Combine(packages, SquirrelPackages.IndexName),
            Path.Combine(packages, ".betaId"),
            Path.Combine(packages, "Chatterbox-1.0.9254-full.nupkg"),
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(3, plan.TargetedPaths.Count);

        foreach (var path in directories.Concat(files))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(directories, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.All(files, f => Assert.True(File.Exists(f), $"{f} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.3, and here it is a refusal rather than a warning. Every Squirrel application shares the
    /// staging folder, so a directory being unpacked into by one application is a directory another
    /// must not remove.
    /// </summary>
    [Fact]
    public async Task AStagingDirectorySomethingIsInstallingThroughIsRefused()
    {
        var busy = Populate(Path.Combine(StagingRoot, "tempa"));
        var idle = Populate(Path.Combine(StagingRoot, "tempb"));

        var provider = CreateProvider(new FakeLiveTreeInspector(busy));
        var plan = await provider.PlanAsync();

        Assert.Equal(idle, Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == busy && p.ExistedBefore);
        Assert.Contains(plan.Notes, n => n.Message.Contains("installing or updating", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(busy), $"{busy} was removed while in use");
    }

    /// <summary>
    /// A check that could not run must not look like a check that found nothing (§5.5's reasoning,
    /// applied to a safeguard rather than a measurement).
    /// </summary>
    [Fact]
    public async Task AMachineWhereLivenessCannotBeEstablishedSaysSo()
    {
        Populate(Path.Combine(StagingRoot, "tempa"));

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check", StringComparison.Ordinal));
    }

    /// <summary>
    /// The staging folder moved onto another drive with a link. Nothing is removed through it, and
    /// the row says so rather than reading as clear.
    /// </summary>
    [Fact]
    public async Task AJunctionedStagingFolderIsLeftAloneAndReported()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere", "tempa"));
        Directory.CreateSymbolicLink(StagingRoot, Path.Combine(_temp.Path, "elsewhere"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
    }

    /// <summary>
    /// §5.2's "never assume a location" applied to the root itself. Squirrel reads
    /// <c>SQUIRREL_TEMP</c> before it falls back to the profile, so a machine that sets it keeps its
    /// staging somewhere else entirely.
    /// </summary>
    [Fact]
    public async Task TheConfiguredStagingFolderIsUsedInsteadOfTheDefault()
    {
        var elsewhere = Path.Combine(_temp.Path, "squirrel-staging");
        var configured = Populate(Path.Combine(elsewhere, "tempa"));
        var untouched = Populate(Path.Combine(StagingRoot, "tempa"));

        _environment.WithEnvironmentVariable(SquirrelDiscovery.StagingVariable, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(configured, Assert.Single(plan.TargetedPaths));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(untouched), $"{untouched} was removed");
    }

    /// <summary>
    /// A configured value that is not a full path. Squirrel resolves it against whichever process is
    /// updating, which Deguffer is not — so the folder is declined by name rather than guessed at,
    /// and the default is not quietly used in its place.
    /// </summary>
    [Fact]
    public async Task AConfiguredStagingFolderThatIsNotAPathIsDeclinedByName()
    {
        Populate(Path.Combine(StagingRoot, "tempa"));
        _environment.WithEnvironmentVariable(SquirrelDiscovery.StagingVariable, "staging");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("'staging'", StringComparison.Ordinal));
    }

    /// <summary>
    /// G4: the profile is swept once for the life of a planning pass, however many questions are put
    /// to the provider — and again after an invalidation, so an application installed while the app
    /// was open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheProfileIsSweptOncePerPassAndAgainAfterInvalidation()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var root = CreateApplication("Chatterbox", "1.0.9254");
        CreatePackages(root, ["Chatterbox-1.0.9254-full.nupkg"], "Chatterbox-1.0.9007-full.nupkg");

        Assert.False(await provider.IsPresentAsync());

        provider.InvalidateCaches();

        Assert.True(await provider.IsPresentAsync());
        Assert.Single((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// Identification is the updater <em>and</em> a build directory. A folder with only one of them
    /// is not established as a Squirrel application, so its packages folder is never reached into —
    /// reclaiming nothing being the safe direction to be wrong in.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AFolderMissingHalfTheIdentificationIsNeverReachedInto(bool updater, bool build)
    {
        var root = Path.Combine(_environment.LocalAppData, "NotSquirrel");
        Directory.CreateDirectory(root);

        if (updater)
        {
            File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        }

        if (build)
        {
            Populate(Path.Combine(root, "app-1.0.0"));
        }

        var packages = CreatePackages(root, ["Other-2.0.0-full.nupkg"], "Other-1.0.0-full.nupkg");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(
            File.Exists(Path.Combine(packages, "Other-1.0.0-full.nupkg")),
            "a package was removed from a folder that is not a Squirrel application");
    }
}
