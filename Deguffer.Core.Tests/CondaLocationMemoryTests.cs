using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// conda's <c>info --json</c> answer is remembered across passes and Explore builds while everything
/// conda read to give it is unchanged, and asked again the moment any of it changes (#447).
///
/// <para>Every provider here shares one <see cref="ToolAnswerMemory"/> whose clock runs a minute ahead
/// of the files the fixture writes, as a real ask starts long after a configuration file was saved, so
/// an unchanged answer is always kept and only a changed input can explain a second ask.</para>
/// </summary>
public sealed class CondaLocationMemoryTests : IDisposable
{
    private const string Info = "info --json";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner = new();
    private readonly string _conda;

    public CondaLocationMemoryTests()
    {
        _conda = Path.Combine(_temp.CreateDirectory("bin"), "conda.exe");
        File.WriteAllText(_conda, "conda 24.1");

        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("conda", _conda);
        _system = new FakeSystemDirectories(_temp.Path);
        _memory = new ToolAnswerMemory(_clock);

        Populate(Path.Combine(PackageCache(Miniconda), "numpy-1.26.4-py312"));
        Write(Path.Combine(Miniconda, "conda-meta", "python-3.12.0.json"), "{}");
        Answering(Miniconda);
        _runner.Responding(_conda, "--dry-run", "{ \"success\": true, \"tarballs\": { \"total_size\": 1000 }, \"packages\": { \"total_size\": 9000 } }");
    }

    public void Dispose() => _temp.Dispose();

    private string Miniconda => Path.Combine(_environment.UserProfile, "miniconda3");

    private string Miniforge => Path.Combine(_environment.UserProfile, "miniforge3");

    private static string PackageCache(string prefix) => Path.Combine(prefix, "pkgs");

    private static string Environments(string prefix) => Path.Combine(prefix, "envs");

    private int Asked => _runner.Invocations.Count(invocation => invocation.Arguments == Info);

    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    /// <summary>conda's answer from now on: an installation at <paramref name="prefix"/>.</summary>
    private void Answering(string prefix) =>
        _runner.Responding(
            _conda,
            Info,
            "{ \"root_prefix\": " + Quote(prefix)
            + ", \"pkgs_dirs\": [" + Quote(PackageCache(prefix)) + "]"
            + ", \"envs_dirs\": [" + Quote(Environments(prefix)) + "] }");

    /// <summary>
    /// Every write happens before the first ask unless the test makes it after, so the clock is moved
    /// a minute past the moment it was called each time a provider is built.
    /// </summary>
    private CondaCacheProvider CreateProvider()
    {
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));

        return new CondaCacheProvider(_environment, _runner, FakeProcessInspector.NothingRunning, systemDirectories: _system, memory: _memory);
    }

    private static string Populate(string directory)
    {
        Write(Path.Combine(directory, "payload.bin"), new string('x', 4096));
        return directory;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// The variables that put a folder on conda's search path, each naming a folder of the temp tree,
    /// so every declared input below has a place on the disk.
    /// </summary>
    private void SetEveryVariable() =>
        _environment
            .WithToolEnvironmentVariable("CONDA_ROOT", Path.Combine(_temp.Path, "condaroot"))
            .WithToolEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_temp.Path, "xdg"))
            .WithToolEnvironmentVariable("CONDA_PREFIX", Path.Combine(_temp.Path, "active"))
            .WithToolEnvironmentVariable("CONDARC", Path.Combine(_temp.Path, "custom", "condarc.yml"));

    [Fact]
    public async Task ASecondPassAfterAnInvalidationWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(1, Asked);
        Assert.Equal([PackageCache(Miniconda)], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }

    public enum Change
    {
        Create,
        Write,
        Delete,
    }

    /// <summary>Every input conda reads to say where it keeps things, relative to the temp tree.</summary>
    public static TheoryData<string, Change> DeclaredInputs()
    {
        string[] inputs =
        [
            @"profile\miniconda3\conda-meta\conda-24.1.0.json",
            @"ProgramData\conda\.condarc",
            @"ProgramData\conda\condarc",
            @"ProgramData\conda\condarc.d\channels.yml",
            @"condaroot\.condarc",
            @"condaroot\condarc",
            @"condaroot\condarc.d\channels.yml",
            @"xdg\conda\.condarc",
            @"xdg\conda\condarc",
            @"xdg\conda\condarc.d\channels.yml",
            @"profile\.config\conda\.condarc",
            @"profile\.config\conda\condarc",
            @"profile\.config\conda\condarc.d\channels.yml",
            @"profile\.conda\.condarc",
            @"profile\.conda\condarc",
            @"profile\.conda\condarc.d\channels.yml",
            @"profile\miniconda3\.condarc",
            @"profile\miniconda3\condarc",
            @"profile\miniconda3\condarc.d\channels.yml",
            @"active\.condarc",
            @"active\condarc",
            @"active\condarc.d\channels.yml",
            @"profile\.condarc",
            @"custom\condarc.yml",
        ];

        var data = new TheoryData<string, Change>();

        foreach (var input in inputs)
        {
            foreach (var change in Enum.GetValues<Change>())
            {
                data.Add(input, change);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input, Change change)
    {
        SetEveryVariable();
        var path = Path.Combine(_temp.Path, input);

        if (change is not Change.Create)
        {
            Write(path, "channels: [defaults]");
        }

        var provider = CreateProvider();
        await provider.DiscoverToolRootsAsync();
        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();
        Assert.Equal(1, Asked);

        switch (change)
        {
            case Change.Create:
            case Change.Write:
                Write(path, "channels: [conda-forge, defaults]");
                break;
            case Change.Delete:
                File.Delete(path);
                break;
        }

        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// conda substitutes <c>CONDARC</c> once, then expands a leading <c>~</c>, and opens a relative
    /// result from the folder it runs in; a folder it names is read as the YAML files in it. So the path
    /// conda opens is the one stamped, under <c>~</c> (<c>profile</c>) or the working folder
    /// (<c>work</c>) as the value says.
    /// </summary>
    [Theory]
    [InlineData(@"~\custom.condarc", @"profile\custom.condarc")]
    [InlineData("custom.condarc", @"work\custom.condarc")]
    [InlineData(@"~\condarcs", @"profile\condarcs\channels.yml")]
    [InlineData("", @"work\channels.yml")]
    public async Task EditingWhatCondarcNamesAsksAgain(string value, string written)
    {
        _environment.WithToolEnvironmentVariable("CONDARC", value);
        var path = written.StartsWith("work", StringComparison.Ordinal)
            ? Path.Combine(ToolInputs.WorkingDirectory(_environment), written[5..])
            : Path.Combine(_environment.UserProfile, written[8..]);
        Directory.CreateDirectory(ToolInputs.WorkingDirectory(_environment));

        var provider = CreateProvider();
        await provider.DiscoverToolRootsAsync();
        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();
        Assert.Equal(1, Asked);

        Write(path, "pkgs_dirs: []");
        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        var provider = CreateProvider();
        await provider.DiscoverToolRootsAsync();

        _environment.WithToolEnvironmentVariable("CONDA_PKGS_DIRS", Path.Combine(_temp.Path, "elsewhere"));
        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task TheExecutableReplacedAsksAgain()
    {
        var provider = CreateProvider();
        await provider.DiscoverToolRootsAsync();

        File.WriteAllText(_conda, "conda 24.11, a newer build");
        provider.InvalidateCaches();
        await provider.DiscoverToolRootsAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second build is answered from the
    /// memory the first filled, and still refuses everything conda reported.
    /// </summary>
    [Fact]
    public async Task ASecondExploreBuildAnswersFromMemoryWithoutAskingConda()
    {
        var first = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [CreateProvider()]);
        var second = CreateProvider();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [second]);
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(1, Asked);
        Assert.False(first.MayRemove(Miniconda).IsAllowed);
        Assert.False(policy.MayRemove(Miniconda).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(PackageCache(Miniconda), "numpy-1.26.4-py312")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Environments(Miniconda), "science")).IsAllowed);
        Assert.Contains(places, place => place.Path.Equals(PackageCache(Miniconda), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6 when the installation moved between two passes: a <c>.condarc</c> edit is what tells the
    /// memory to ask again, and the plan then measures and protects the new installation, and nothing
    /// of the old one is left in it as a target or as a survivor that would mask the new one.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheInstallationMovedTargetsAndProtectsTheNewOne()
    {
        Populate(Environments(Miniconda));
        Populate(Path.Combine(PackageCache(Miniforge), "scipy-1.13.0-py312"));
        var environments = Populate(Path.Combine(Environments(Miniforge), "science"));
        var provider = CreateProvider();
        await provider.PlanAsync();

        Write(Path.Combine(_environment.UserProfile, ".condarc"), "root_prefix: miniforge3");
        Answering(Miniforge);
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(2, Asked);
        Assert.Equal([PackageCache(Miniforge)], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.Empty(plan.TargetedPaths);

        foreach (var survivor in (string[])[Miniforge, Environments(Miniforge), PackageCache(Miniforge), Path.Combine(_environment.UserProfile, ".condarc")])
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => LongPath.Contains(Miniconda, p.Path));

        // The command overreaching into the new installation's environments is caught.
        Directory.Delete(environments, recursive: true);
        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, failure => LongPath.Contains(failure.Subject, environments));
    }

    [Fact]
    public async Task AFailedQueryIsNotKept()
    {
        _runner.Responding(_conda, Info, "{}", exitCode: 1);
        var provider = CreateProvider();
        Assert.Contains((await provider.PlanAsync()).Notes, n => n.Message.Contains("conda did not describe", StringComparison.Ordinal));

        Answering(Miniconda);
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(2, Asked);
        Assert.Equal([PackageCache(Miniconda)], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
    }
}
