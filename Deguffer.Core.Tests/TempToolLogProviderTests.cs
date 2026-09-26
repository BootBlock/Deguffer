using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Logs are emptied from the folders they are written into, and those folders, the tools' folders
/// around them and everything else in them survive (§5.2, §5.6).
/// </summary>
public sealed class TempToolLogProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TempToolLogProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempToolLogProvider CreateProvider() =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive);

    private string Entry(int bytes, params string[] segments) => _temp.CreateFile(bytes, ["temp", .. segments]);

    [Fact]
    public void IsTierThree()
    {
        Assert.Equal(SafetyTier.UserData, CreateProvider().Tier);
    }

    [Fact]
    public async Task EmptiesTheLogFoldersAndLeavesEverythingAroundThem()
    {
        Entry(4096, "DiagOutputDir", "RdClientAutoTrace", "RdClientAutoTrace-WppAutoTrace-20260901.etl");
        Entry(2048, "DiagOutputDir", "Windows365", "Logs", "health_checks.log");
        Entry(1024, "servicehub", "logs", "0a1b2c3d-VsHubClient-1234-abcdefgh-1.log");
        Entry(512, "vscode-inno-updater-1756000000.log");
        var diagOther = Entry(256, "DiagOutputDir", "OtherClient", "trace.etl");
        var hubOther = Entry(128, "servicehub", "settings.json");
        var telemetry = Entry(64, "VSTelem", "NgenPdb", "x.pdb");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(4096 + 2048 + 1024 + 512, plan.EstimatedBytes);
        Assert.Equal(3, plan.Steps.OfType<ClearDirectoryStep>().Count());
        Assert.Single(plan.Steps.OfType<DeleteFileStep>());

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Path.Combine(UserTemp, "DiagOutputDir", "RdClientAutoTrace")), "a log folder was removed rather than emptied");
        Assert.True(Directory.Exists(Path.Combine(UserTemp, "servicehub", "logs")), "a log folder was removed rather than emptied");
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(UserTemp, "servicehub", "logs")));
        Assert.True(File.Exists(diagOther), "something else in the shared diagnostics folder was removed");
        Assert.True(File.Exists(hubOther), "something in ServiceHub's folder besides its logs was removed");
        Assert.True(File.Exists(telemetry), "Visual Studio's telemetry folder was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task ClaimsTheToolsFoldersWholeAndTheUpdaterLog()
    {
        Entry(1, "DiagOutputDir", "RdClientAutoTrace", "a.etl");
        Entry(1, "servicehub", "logs", "a.log");
        var log = Entry(1, "vscode-inno-updater-1756000000.log");
        Entry(1, "VSTelem", "x");

        var claimed = await CreateProvider().ClaimedEntriesAsync([UserTemp]);

        Assert.Equal(
            new[] { Path.Combine(UserTemp, "DiagOutputDir"), Path.Combine(UserTemp, "servicehub"), log }.Order(StringComparer.Ordinal),
            claimed.Order(StringComparer.Ordinal));
    }
}
