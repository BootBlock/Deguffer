using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// NuGet's answer about where it keeps its caches outlives a pass while everything
/// <c>dotnet nuget locals all --list</c> read to give it reads the same (#1059), and is asked again the
/// moment any of it does not. The plan sends only NuGet's own clear, so it plans from the memory; the
/// temporary-folder row's claim spares <c>NuGetScratch</c> from a path-based deletion, so it asks
/// NuGet afresh every time.
///
/// <para>Every provider here shares one <see cref="ToolAnswerMemory"/> whose clock runs a minute ahead
/// of the files the fixture writes, as a real ask starts long after a configuration file was saved.
/// Each "asks again" case starts from a trusted answer (<see cref="TrustAsync"/>), so only the change
/// it names can explain the ask.</para>
/// </summary>
public sealed class NuGetLocationMemoryTests : IDisposable
{
    private const string List = "nuget locals all --list";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner = new();
    private readonly string _dotnet;
    private int _since;

    public NuGetLocationMemoryTests()
    {
        _dotnet = Path.Combine(_temp.CreateDirectory("dotnet"), "dotnet.exe");
        File.WriteAllText(_dotnet, "dotnet host");
        _temp.CreateDirectory("dotnet", "sdk", "10.0.100");

        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("dotnet", _dotnet);
        _system = new FakeSystemDirectories(_temp.CreateDirectory("machine"));
        _memory = new ToolAnswerMemory(_clock);

        Write(UserConfig, "<configuration />");
        Answering(CreateLocals("first"));
    }

    public void Dispose() => _temp.Dispose();

    private string UserConfig => Path.Combine(_environment.RoamingAppData, "NuGet", "NuGet.Config");

    /// <summary>How often NuGet was asked where its caches are since its answer was last trusted.</summary>
    private int Asked => _runner.Invocations.Skip(_since).Count(invocation => invocation.Arguments == List);

    private string Key => $"{List}|{_dotnet}";

    /// <summary>A packages folder and a scratch folder in the temporary folder, each holding a file.</summary>
    private (string Packages, string Scratch) CreateLocals(string name)
    {
        var packages = _temp.CreateDirectory(name, "packages");
        File.WriteAllBytes(Path.Combine(packages, "payload.nupkg"), new byte[4096]);

        return (packages, _temp.CreateDirectory("temp", name + "-NuGetScratch"));
    }

    /// <summary>NuGet's answer from now on.</summary>
    private void Answering((string Packages, string Scratch) locals) =>
        _runner.Responding(_dotnet, "locals all --list", $"global-packages: {locals.Packages}\\\ntemp: {locals.Scratch}\\");

    private NuGetCacheProvider CreateProvider()
    {
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));

        return new NuGetCacheProvider(
            _environment, _runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory(), systemDirectories: _system, memory: _memory);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static async Task SecondPassAsync(NuGetCacheProvider provider)
    {
        provider.InvalidateCaches();
        await provider.PlanAsync();
    }

    /// <summary>
    /// Passes until NuGet's answer is trusted: the first holds it, the second confirms it, and a third
    /// with nothing changed must ask nothing. The count then starts again.
    /// </summary>
    private async Task TrustAsync(NuGetCacheProvider provider)
    {
        await provider.PlanAsync();
        await SecondPassAsync(provider);
        Assert.True(_memory.Holds(Key));

        await SecondPassAsync(provider);
        Assert.Equal(2, Asked);

        _since = _runner.Invocations.Count;
    }

    /// <summary>
    /// A confirmed answer is a list, which a record compares by reference: without a comparison of its
    /// entries, the next ask would never give an answer equal to the last, and nothing would be served.
    /// </summary>
    [Fact]
    public async Task APassOnceTheAnswerIsConfirmedWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();
        await provider.PlanAsync();
        Assert.False(_memory.Holds(Key));
        await SecondPassAsync(provider);
        Assert.True(_memory.Holds(Key));

        await SecondPassAsync(provider);

        Assert.Equal(2, Asked);
    }

    public enum Change
    {
        Create,
        CreateFolder,
        Write,
        Delete,
    }

    /// <summary>
    /// Every place NuGet reads its configuration or its code from, relative to the temp tree: the
    /// user's configuration and the folder beside it, the machine-wide folder, the SDKs beside the
    /// host, and a <c>NuGet.Config</c> or <c>global.json</c> in the folder it runs in and every one above
    /// it (sampled here at the working folder itself, the profile and the temp root).
    /// </summary>
    public static TheoryData<string, Change> DeclaredInputs => new()
    {
        { @"profile\AppData\Roaming\NuGet\NuGet.Config", Change.Write },
        { @"profile\AppData\Roaming\NuGet\NuGet.Config", Change.Delete },
        { @"profile\AppData\Roaming\NuGet\config\company.config", Change.Create },
        { @"machine\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.Offline.config", Change.Create },
        { @"profile\AppData\Local\Deguffer\tool-working-directory\NuGet.Config", Change.Create },
        { @"profile\AppData\Local\Deguffer\tool-working-directory\global.json", Change.Create },
        { @"profile\nuget.config", Change.Create },
        { @"profile\global.json", Change.Create },
        { "NuGet.Config", Change.Create },
        { "global.json", Change.Create },
        { @"dotnet\sdk\10.0.200", Change.CreateFolder },
        { @"dotnet\sdk\10.0.100", Change.Delete },
        { @"dotnet\dotnet.exe", Change.Write },
    };

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string relative, Change change)
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        var path = Path.Combine(_temp.Path, relative);

        switch (change)
        {
            case Change.Create:
                Write(path, "<configuration />");
                break;
            case Change.CreateFolder:
                Directory.CreateDirectory(path);
                break;
            case Change.Write:
                File.AppendAllText(path, "<!-- a longer file than before -->");
                break;
            case Change.Delete when Directory.Exists(path):
                Directory.Delete(path, recursive: true);
                break;
            case Change.Delete:
                File.Delete(path);
                break;
        }

        await SecondPassAsync(provider);

        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        _environment.WithToolEnvironmentVariable("NUGET_PACKAGES", Path.Combine(_temp.Path, "elsewhere"));
        await SecondPassAsync(provider);

        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task AnAnswerNuGetDidNotGiveIsNotKept()
    {
        _runner.Responding(_dotnet, "locals all --list", string.Empty, exitCode: 1);
        var provider = CreateProvider();

        await provider.PlanAsync();
        await SecondPassAsync(provider);
        await SecondPassAsync(provider);

        Assert.Equal(3, Asked);
        Assert.False(_memory.Holds(Key));
    }

    /// <summary>
    /// Explore builds fresh providers for each policy, and a duplicate search asks each where its tool
    /// cleans, so the answer one heard and confirmed must reach the next through the memory they share.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersWhereItCleansWithoutAsking()
    {
        var moved = CreateLocals("second");
        Answering(moved);
        await TrustAsync(CreateProvider());

        var places = await CreateProvider().CleanedPlacesAsync();

        Assert.Equal(0, Asked);
        Assert.Contains(places, place => place.Path.Equals(moved.Packages, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The claim spares <c>NuGetScratch</c> from the temporary-folder row's deletion by path, so it
    /// asks NuGet every time, even with a trusted answer held and every input unchanged. Here something
    /// no declared input records has moved the scratch folder: a claim from the memory would leave the
    /// new one to the other row's deletion (§5.2).
    /// </summary>
    [Fact]
    public async Task TheClaimAsksNuGetAfreshAndFollowsAScratchFolderTheMemoryMissed()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        var moved = CreateLocals("moved");
        Answering(moved);
        provider.InvalidateCaches();
        var claimed = await provider.ClaimedEntriesAsync([_environment.TempPath]);

        Assert.Equal(1, Asked);
        Assert.Equal([moved.Scratch], claimed);

        for (var pass = 2; pass <= 3; pass++)
        {
            provider.InvalidateCaches();
            Assert.Equal([moved.Scratch], await provider.ClaimedEntriesAsync([_environment.TempPath]));
            Assert.Equal(pass, Asked);
        }
    }

    /// <summary>
    /// The plan in a pass whose claim has already asked afresh plans from the memory, which the fresh
    /// answer replaced, and one ask of NuGet serves the pass.
    /// </summary>
    [Fact]
    public async Task ThePlanAfterTheClaimInOnePassAsksNuGetOnlyOnce()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);
        provider.InvalidateCaches();

        await provider.ClaimedEntriesAsync([_environment.TempPath]);
        await provider.PlanAsync();

        Assert.Equal(1, Asked);
    }

    /// <summary>
    /// §5.6 when the caches moved between two passes, as a <c>NuGet.Config</c> edit moves them: the clear
    /// measures the new packages folder and not the old, nothing is targeted, and NuGet's configuration
    /// and its own folders are still named as what must survive.
    /// </summary>
    [Fact]
    public async Task ThePlanFollowsTheCachesWhenTheyMovedBetweenTwoPasses()
    {
        var first = CreateLocals("first");
        var provider = CreateProvider();
        await TrustAsync(provider);

        var moved = CreateLocals("moved");
        Write(UserConfig, $"<configuration><config><add key=\"globalPackagesFolder\" value=\"{moved.Packages}\" /></config></configuration>");
        Answering(moved);
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal(1, Asked);
        Assert.Empty(plan.TargetedPaths);
        var clear = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains(moved.Packages, clear.MeasuredPaths);
        Assert.DoesNotContain(first.Packages, clear.MeasuredPaths);

        string[] survivors =
        [
            UserConfig,
            Path.Combine(_environment.UserProfile, ".nuget"),
            Path.Combine(_environment.LocalAppData, "NuGet"),
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(UserConfig, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }
}
