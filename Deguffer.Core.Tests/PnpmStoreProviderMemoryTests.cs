using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where pnpm said its store is outlives a pass for as long as everything pnpm read to say so is
/// unchanged (#447), and is asked again the moment any of it is not. Every declared input has a case
/// of its own, so an input dropped from <see cref="PnpmStoreAnswer.InputsFor"/> fails by name.
///
/// <para>The clock the memory reads is an hour ahead of the fixture, as a real ask starts long after
/// a configuration file was last saved, so an answer is kept rather than refused as read mid-write.</para>
/// </summary>
public sealed class PnpmStoreProviderMemoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly string _pnpm;
    private readonly FakeProcessRunner _runner;

    public PnpmStoreProviderMemoryTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _pnpm = Write(Path.Combine("AppData", "Roaming", "npm", "pnpm.cmd"), "@node pnpm.cjs %*");
        _environment
            .WithExecutable("pnpm", _pnpm)
            .WithExecutable("node", Write(Path.Combine("nodejs", "node.exe"), "node"))
            .WithEnvironmentVariable("XDG_CONFIG_HOME", InProfile(".config"))
            .WithEnvironmentVariable("npm_config_userconfig", InProfile("custom.npmrc"));
        Populate(Store);
        Directory.CreateDirectory(Path.Combine(Home, "global"));

        _runner = new FakeProcessRunner()
            .Responding(_pnpm, "store path", Store + "\r\n")
            .Responding(_pnpm, "config get globalconfig", GlobalConfig + "\r\n");

        _memory = new ToolAnswerMemory(_clock);
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromHours(1));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>What happens to a declared input between two passes.</summary>
    public enum Change
    {
        Created,
        Rewritten,
        Deleted,
    }

    private string Home => Path.Combine(_environment.LocalAppData, "pnpm");

    private string Store => Path.Combine(Home, "store", "v10");

    private string GlobalConfig => InProfile(Path.Combine("AppData", "Roaming", "npm", "etc", "npmrc"));

    private int StoreAsks => _runner.Invocations.Count(i => i.Arguments == "store path");

    private int GlobalConfigAsks => _runner.Invocations.Count(i => i.Arguments == "config get globalconfig");

    /// <summary>Every file pnpm reads with no variable moving it, relative to the profile.</summary>
    public static TheoryData<string, Change> DeclaredInputs() => Cases(
        Enum.GetValues<Change>(),
        Path.Combine("AppData", "Roaming", "npm", "node.exe"),
        Path.Combine("AppData", "Roaming", "npm", "node_modules", "pnpm", "package.json"),
        Path.Combine("nodejs", "node.exe"),
        Path.Combine("AppData", "Local", "pnpm", "config", "rc"),
        Path.Combine("AppData", "Local", "pnpm", "config", "config.yaml"),
        Path.Combine("AppData", "Local", "pnpm", "config", "auth.ini"),
        Path.Combine(".config", "pnpm", "rc"),
        Path.Combine(".config", "pnpm", "config.yaml"),
        Path.Combine(".config", "pnpm", "auth.ini"),
        ".npmrc",
        "custom.npmrc",
        Path.Combine("AppData", "Roaming", "npm", "etc", "npmrc"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", ".npmrc"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", "pnpm-workspace.yaml"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", "package.json"),
        Path.Combine("AppData", "Local", "Deguffer", "pnpm-workspace.yaml"),
        "package.json",
        Path.Combine("AppData", "Local", "pnpm", ".tools"),
        Path.Combine("AppData", "Local", "node", "corepack", "lastKnownGood.json"));

    /// <summary>
    /// A pnpm version Corepack downloaded, two levels into its home. Only its arrival and removal
    /// show in that listing: what is written inside a version's folder does not change which pnpm runs.
    /// </summary>
    public static TheoryData<string, Change> CorepackVersions() => Cases(
        [Change.Created, Change.Deleted],
        Path.Combine("AppData", "Local", "node", "corepack", "v1", "pnpm", "10.0.0"));

    /// <summary>Corepack's and pnpm's homes where a variable moved them, relative to the profile.</summary>
    public static TheoryData<string, Change> MovedHomes() => Cases(
        Enum.GetValues<Change>(),
        Path.Combine("moved-pnpm", ".tools"),
        Path.Combine("moved-corepack", "lastKnownGood.json"));

    /// <summary>A pnpm version Corepack downloaded into a home a variable moved, as <see cref="CorepackVersions"/>.</summary>
    public static TheoryData<string, Change> MovedCorepackVersions() => Cases(
        [Change.Created, Change.Deleted],
        Path.Combine("moved-corepack", "v1", "pnpm", "10.0.0"));

    [Fact]
    public async Task ASecondPassWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(1, StoreAsks);
        Assert.Equal(1, GlobalConfigAsks);
        Assert.Equal([Store], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    [MemberData(nameof(CorepackVersions))]
    public async Task AChangedInputAsksAgain(string relative, Change change) =>
        await AssertAsksAgainAfterAsync(relative, change);

    [Theory]
    [MemberData(nameof(MovedHomes))]
    [MemberData(nameof(MovedCorepackVersions))]
    public async Task AChangedInputUnderAMovedHomeAsksAgain(string relative, Change change)
    {
        _environment
            .WithEnvironmentVariable("PNPM_HOME", InProfile("moved-pnpm"))
            .WithEnvironmentVariable("COREPACK_HOME", InProfile("moved-corepack"));

        await AssertAsksAgainAfterAsync(relative, change);
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        var provider = CreateProvider();
        await provider.PlanAsync();

        _environment.WithToolEnvironmentVariable("npm_config_store_dir", InProfile("elsewhere"));
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
    }

    [Fact]
    public async Task TheExecutableReplacedAsksAgain()
    {
        var provider = CreateProvider();
        await provider.PlanAsync();

        // A copy keeping the old file's size and last-write time, as an installer may leave it.
        var replacement = Path.Combine(_temp.Path, "pnpm.cmd.new");
        File.Copy(_pnpm, replacement);
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(_pnpm));
        File.SetCreationTimeUtc(replacement, File.GetCreationTimeUtc(_pnpm).AddSeconds(7));
        File.Move(replacement, _pnpm, overwrite: true);

        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
    }

    /// <summary>
    /// pnpm opens a relative folder from the folder it runs in, so that is the folder stamped: a
    /// settings file written there asks again, and one with the same name beside Deguffer does not
    /// matter.
    /// </summary>
    [Fact]
    public async Task AConfigurationFolderThatIsNotAFullPathIsReadFromTheToolsWorkingFolder()
    {
        _environment.WithEnvironmentVariable("XDG_CONFIG_HOME", "relative-config");
        var provider = CreateProvider();

        await provider.PlanAsync();
        var rc = Path.Combine(ToolInputs.WorkingDirectory(_environment), "relative-config", "pnpm", "rc");
        Directory.CreateDirectory(Path.GetDirectoryName(rc)!);
        File.WriteAllText(rc, "store-dir=D:\elsewhere");
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
    }

    [Fact]
    public async Task AConfigurationFolderThatIsNotAFullPathIsRememberedWhileNothingInItChanges()
    {
        _environment.WithEnvironmentVariable("XDG_CONFIG_HOME", "relative-config");
        var provider = CreateProvider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(1, StoreAsks);
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy, so the second set must be answered
    /// from what the first heard rather than start pnpm again.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAsksNothing()
    {
        await CreateProvider().PlanAsync();

        var second = CreateProvider();
        var roots = await second.DiscoverToolRootsAsync();
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(1, StoreAsks);
        Assert.Contains(roots, r => r.Path.Equals(Store, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(places, p => p.Path.Equals(Store, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §7.1: each Explore policy build constructs its providers afresh, and the second build refuses
    /// the store as the first did without starting pnpm again.
    /// </summary>
    [Fact]
    public async Task ASecondExplorePolicyBuildAnswersFromMemoryWithoutAskingPnpm()
    {
        var system = new FakeSystemDirectories(_temp.CreateDirectory("machine"));

        var first = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);
        var second = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.Equal(1, StoreAsks);
        Assert.False(first.MayRemove(Store).IsAllowed);
        Assert.False(second.MayRemove(Store).IsAllowed);
    }

    /// <summary>
    /// §5.6 when <c>store-dir</c> moved the store between two passes: the plan measures and protects
    /// the store pnpm now reports, and the remembered one is gone from it.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheStoreMovedTargetsAndProtectsTheNewStore()
    {
        var moved = Populate(InProfile(Path.Combine(".pnpm-store", "v10")));
        var provider = CreateProvider();
        await provider.PlanAsync();

        Write(".npmrc", $"store-dir={Path.GetDirectoryName(moved)}");
        _runner.Responding(_pnpm, "store path", moved + "\r\n");
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
        Assert.Empty(plan.TargetedPaths);
        Assert.Equal([moved], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);

        foreach (var path in (string[])[moved, Path.GetDirectoryName(moved)!, Home, Path.Combine(Home, "global")])
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(Store, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(moved, Assert.Single(await provider.DiscoverToolRootsAsync()).Path);

        Directory.Delete(moved, recursive: true);
        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFailedStoreQueryIsNotKept()
    {
        _runner.Responding(_pnpm, "store path", Store, exitCode: 1);
        var provider = CreateProvider();

        Assert.True((await provider.PlanAsync()).WasNotExamined);
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
    }

    /// <summary>
    /// Without the global configuration's path an edit to that file would go unnoticed, so the store
    /// serves the pass that asked and the next pass asks again.
    /// </summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("undefined", 0)]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    public async Task AStoreWithoutItsGlobalConfigurationServesThePassButIsNotKept(string? printed, int exitCode)
    {
        // Null stands for the real global configuration's path, printed by a query that failed: a
        // failed query is no answer, whatever it printed.
        _runner.Responding(_pnpm, "config get globalconfig", printed ?? GlobalConfig, exitCode);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal([Store], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.Equal(2, StoreAsks);
    }

    private static TheoryData<string, Change> Cases(IReadOnlyList<Change> changes, params string[] paths)
    {
        var cases = new TheoryData<string, Change>();

        foreach (var path in paths)
        {
            foreach (var change in changes)
            {
                cases.Add(path, change);
            }
        }

        return cases;
    }

    /// <summary>
    /// A pass, a second pass that must ask nothing (so the case proves the answer was kept), the
    /// change, and a third pass that must ask again. The input's folder exists from the start, so no
    /// folder appears beside the input. A rewrite changes nothing but the input, so it is the case
    /// that fails by name where a file inside a listed folder drops out of the declaration.
    /// </summary>
    private async Task AssertAsksAgainAfterAsync(string relative, Change change)
    {
        var path = InProfile(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (change is not Change.Created)
        {
            Write(relative, "before");
        }

        var provider = CreateProvider();
        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(1, StoreAsks);

        switch (change)
        {
            case Change.Created:
            case Change.Rewritten:
                Write(relative, "after, and longer");
                break;
            case Change.Deleted:
                File.Delete(path);
                break;
        }

        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, StoreAsks);
    }

    /// <summary>A provider on the shared memory.</summary>
    private PnpmStoreProvider CreateProvider() =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, memory: _memory);

    private string InProfile(string relative) => Path.Combine(_environment.UserProfile, relative);

    private string Write(string relative, string content)
    {
        var path = InProfile(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Populate(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[4096]);
        return directory;
    }
}
