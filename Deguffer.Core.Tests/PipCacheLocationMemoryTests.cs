using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// pip's answer about where its cache is outlives a pass while everything <c>pip cache dir</c> read
/// to give it reads the same (#447), and is asked again the moment any of it does not.
///
/// <para>The pip here is a real launcher in the temp tree naming a real interpreter file, because the
/// launcher is how the provider learns <c>sys.prefix</c>, and a launcher it cannot read is never
/// remembered.</para>
///
/// <para>Each "asks again" case starts from a trusted answer (<see cref="TrustAsync"/>), so only the
/// change it names can explain the ask, and each "not kept" case asks three times, because a kept
/// answer would be confirmed by the second ask and served to the third.</para>
/// </summary>
public sealed class PipCacheLocationMemoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner;
    private readonly string _pip;
    private readonly string _interpreter;
    private string _answer;
    private int _exitCode;
    private int _since;

    public PipCacheLocationMemoryTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.CreateDirectory("machine"));
        _memory = new ToolAnswerMemory(_clock);

        _interpreter = Path.Combine(_temp.Path, "Python312", "python.exe");
        _temp.CreateFile(64, "Python312", "python.exe");
        _temp.CreateDirectory("Python312", "Lib", "site-packages", "pip-24.0.dist-info");
        _temp.CreateDirectory("profile", "AppData", "Roaming", "Python", "Python312", "site-packages");

        File.WriteAllText(Path.Combine(_environment.RoamingAppData, "pip", "pip.ini").Created(), "[global]\n");

        _pip = Path.Combine(_temp.Path, "Python312", "Scripts", "pip.exe");
        DistlibLaunchers.Write(_pip, _interpreter);
        _environment.WithExecutable("pip", _pip);

        _answer = CreateCache("first-cache");
        _runner = new FakeProcessRunner().Replying(_pip, arguments =>
            arguments.Contains("cache dir", StringComparison.Ordinal) ? new CommandOutcome(_exitCode, _answer, string.Empty) : null);

        // Every pass starts after the fixture was written, as a real one starts long after pip's
        // configuration was last saved.
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>How often pip was asked where its cache is since its answer was last trusted.</summary>
    private int LocationQueries => _runner.Invocations.Skip(_since).Count(i => i.Arguments.Contains("cache dir", StringComparison.Ordinal));

    private string Key => $"cache dir --no-color|{_pip}";

    private PipCacheProvider CreateProvider() =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, scanner: null, _system, _memory);

    private string CreateCache(string name)
    {
        _temp.CreateFile(4096, name, "http-v2", "payload.bin");
        return Path.Combine(_temp.Path, name);
    }

    private static async Task SecondPassAsync(PipCacheProvider provider)
    {
        provider.InvalidateCaches();
        await provider.PlanAsync();
    }

    /// <summary>
    /// Passes until pip's answer is trusted: the first holds it, the second confirms it, and a third
    /// with nothing changed must ask nothing. The count then starts again.
    /// </summary>
    private async Task TrustAsync(PipCacheProvider provider)
    {
        await provider.PlanAsync();
        await SecondPassAsync(provider);
        Assert.True(_memory.Holds(Key));

        await SecondPassAsync(provider);
        Assert.Equal(2, LocationQueries);

        _since = _runner.Invocations.Count;
    }

    /// <summary>
    /// Three passes that each ask: a kept answer would have been confirmed by the second and served to
    /// the third.
    /// </summary>
    private async Task AssertNeverKeptAsync(PipCacheProvider provider)
    {
        await provider.PlanAsync();
        await SecondPassAsync(provider);
        await SecondPassAsync(provider);

        Assert.Equal(3, LocationQueries);
        Assert.False(_memory.Holds(Key));
    }

    /// <summary>
    /// A <c>PIP_CONFIG_FILE</c> no path can be made of names nothing pip could open, so it is no input,
    /// and it does not stop the plan.
    /// </summary>
    [Fact]
    public async Task AConfigFileVariableNoPathCanHoldDoesNotStopThePlan()
    {
        _environment.WithToolEnvironmentVariable("PIP_CONFIG_FILE", $"pip{(char)0}.ini");
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.Equal(1, LocationQueries);
    }

    [Fact]
    public async Task APassOnceTheAnswerIsConfirmedWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();
        await provider.PlanAsync();
        Assert.False(_memory.Holds(Key));
        await SecondPassAsync(provider);
        Assert.True(_memory.Holds(Key));

        await SecondPassAsync(provider);

        Assert.Equal(2, LocationQueries);
    }

    /// <summary>Each place pip reads its configuration or its code from, relative to the temp tree.</summary>
    public static TheoryData<string, InputChange> DeclaredInputs => new()
    {
        { @"machine\ProgramData\pip\pip.ini", InputChange.Create },
        { @"profile\pip\pip.ini", InputChange.Create },
        { @"profile\AppData\Roaming\pip\pip.ini", InputChange.Write },
        { @"profile\AppData\Roaming\pip\pip.ini", InputChange.Delete },
        { @"Python312\pip.ini", InputChange.Create },
        { @"Python312\pyvenv.cfg", InputChange.Create },
        { "pyvenv.cfg", InputChange.Create },
        { @"Python312\python.exe", InputChange.Write },
        { @"Python312\Lib\site-packages\pip-25.0.dist-info", InputChange.CreateFolder },
        { @"Python312\Lib\site-packages\pip-24.0.dist-info", InputChange.Delete },
        { @"profile\AppData\Roaming\Python\Python312\site-packages\pip-25.0.dist-info", InputChange.CreateFolder },
    };

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string relative, InputChange change)
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        Apply(Path.Combine(_temp.Path, relative), change);
        await SecondPassAsync(provider);

        Assert.Equal(1, LocationQueries);
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        _environment.WithToolEnvironmentVariable("PIP_CACHE_DIR", Path.Combine(_temp.Path, "elsewhere"));
        await SecondPassAsync(provider);

        Assert.Equal(1, LocationQueries);
    }

    [Fact]
    public async Task WritingTheFilePipConfigFileNamesAsksAgain()
    {
        var named = _temp.CreateFile(16, "named", "pip.ini");
        _environment.WithEnvironmentVariable("PIP_CONFIG_FILE", named);
        var provider = CreateProvider();
        await TrustAsync(provider);

        File.WriteAllText(named, "[global]\ncache-dir = elsewhere\n");
        await SecondPassAsync(provider);

        Assert.Equal(1, LocationQueries);
    }

    [Fact]
    public async Task ReplacingTheExecutableAsksAgain()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        // The same interpreter, so only the launcher itself says pip was reinstalled.
        DistlibLaunchers.Write(_pip, _interpreter);
        await SecondPassAsync(provider);

        Assert.Equal(1, LocationQueries);
    }

    /// <summary>
    /// A pip whose launcher names no interpreter leaves <c>sys.prefix</c>, and the two places pip reads
    /// from it, unknown, so nothing it says can be kept.
    /// </summary>
    [Fact]
    public async Task AnswerFromALauncherNamingNoInterpreterIsNotKept()
    {
        File.WriteAllText(_pip, "@echo off\r\npython -m pip %*\r\n");

        await AssertNeverKeptAsync(CreateProvider());
    }

    /// <summary>
    /// Whether the interpreter sits in a virtual environment decides <c>sys.prefix</c>. Where Windows
    /// will not say, the prefix is unknown, and so is what pip read from it.
    /// </summary>
    [Fact]
    public async Task AnAnswerIsNotKeptWhileTheVirtualEnvironmentMarkerCannotBeRead()
    {
        var marker = _temp.CreateFile(16, "pyvenv.cfg");
        using var denied = DeniedDirectory.WithUnreadableFile(marker);
        Assert.Equal(PathPresence.Refused, InputStamp.Of(marker).Presence);

        await AssertNeverKeptAsync(CreateProvider());
    }

    [Fact]
    public async Task AnAnswerPipDidNotGiveIsNotKept()
    {
        _exitCode = 1;

        await AssertNeverKeptAsync(CreateProvider());
    }

    /// <summary>
    /// Explore builds fresh providers for each policy, so the answer one heard and confirmed must reach
    /// the next through the memory they share.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersWhereItCleansWithoutAsking()
    {
        await TrustAsync(CreateProvider());

        var places = await CreateProvider().CleanedPlacesAsync();

        Assert.Equal(0, LocationQueries);
        Assert.Contains(places, place => place.Path == _answer);
    }

    /// <summary>
    /// §5.6 when pip's cache moved between two passes: the purge measures the new cache and not the
    /// old, nothing is targeted for deletion, and pip's own folder and configuration are still named
    /// as what must survive.
    /// </summary>
    [Fact]
    public async Task ThePlanFollowsTheCacheWhenItMovedBetweenTwoPasses()
    {
        var provider = CreateProvider();
        var first = _answer;
        await TrustAsync(provider);

        var roamingConfig = Path.Combine(_environment.RoamingAppData, "pip", "pip.ini");
        _answer = CreateCache("moved-cache");
        File.WriteAllText(roamingConfig, $"[global]\ncache-dir = {_answer}\n");
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(1, LocationQueries);
        Assert.Empty(plan.TargetedPaths);
        Assert.Equal([_answer], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.DoesNotContain(plan.Steps.OfType<RunCommandStep>(), step => step.MeasuredPaths.Contains(first));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == provider.LocalRoot);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == roamingConfig && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(await provider.CleanedPlacesAsync(), place => place.Path == _answer);
        Assert.DoesNotContain(await provider.CleanedPlacesAsync(), place => place.Path == first);
    }

    private static void Apply(string path, InputChange change)
    {
        switch (change)
        {
            case InputChange.Create:
                File.WriteAllText(path.Created(), "[global]\n");
                break;
            case InputChange.CreateFolder:
                Directory.CreateDirectory(path);
                break;
            case InputChange.Write:
                File.AppendAllText(path, "# a longer file than before\n");
                break;
            case InputChange.Delete when Directory.Exists(path):
                Directory.Delete(path, recursive: true);
                break;
            case InputChange.Delete:
                File.Delete(path);
                break;
        }
    }

    /// <summary>What a test does to one declared input.</summary>
    public enum InputChange
    {
        Create,
        CreateFolder,
        Write,
        Delete,
    }
}

/// <summary>Paths a test writes a file to.</summary>
file static class TestPaths
{
    /// <summary><paramref name="path"/>, once its folder exists.</summary>
    public static string Created(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
