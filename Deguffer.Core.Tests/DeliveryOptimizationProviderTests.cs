using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Delivery Optimization cache: Tier 1, sized by Windows and cleared by Windows' own command, and
/// never by deleting a path.
///
/// <para>Everything runs against a synthetic Windows directory and a fake process runner. The real
/// cache is in the Network Service profile, which the suite could not list, and a test that cleared it
/// would be clearing the cache of whoever ran the suite.</para>
/// </summary>
public sealed class DeliveryOptimizationProviderTests : IDisposable
{
    private const long CacheBytes = 5L * 1024 * 1024 * 1024;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();

    public DeliveryOptimizationProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string PowerShellHome =>
        Path.Combine(_system.WindowsDirectory, "System32", "WindowsPowerShell", "v1.0");

    private DeliveryOptimizationProvider CreateProvider() => new(
        _environment,
        _runner,
        FakeProcessInspector.NothingRunning,
        new FakeDirectoryScanner(),
        _system);

    /// <summary>The module's manifest, where Windows ships it.</summary>
    private void InstallModule() =>
        _temp.CreateFile(64, "Windows", "System32", "WindowsPowerShell", "v1.0", "Modules", "DeliveryOptimization",
            "DeliveryOptimization.psd1");

    /// <summary>Windows reporting <paramref name="bytes"/> in the cache, and clearing it successfully.</summary>
    private void ReportCache(long bytes) => _runner
        .Responding("Get-DeliveryOptimizationPerfSnap", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n")
        .Responding("Delete-DeliveryOptimizationCache", "Deleting...\r\nSuccessfully deleted Delivery Optimization cache\r\n");

    /// <summary>
    /// Windows' update folder as it is on a real machine: the update history, the updates waiting to
    /// install, and a file beside them. The command reaches none of it.
    /// </summary>
    private (string SoftwareDistribution, string DataStore, string Download) CreateSoftwareDistribution()
    {
        var root = _temp.CreateDirectory("Windows", "SoftwareDistribution");
        _temp.CreateFile(4096, "Windows", "SoftwareDistribution", "DataStore", "DataStore.edb");
        _temp.CreateFile(2048, "Windows", "SoftwareDistribution", "Download", "pending.cab");
        _temp.CreateFile(128, "Windows", "SoftwareDistribution", "ReportingEvents.log");

        return (root, Path.Combine(root, "DataStore"), Path.Combine(root, "Download"));
    }

    [Fact]
    public async Task ItIsPresentWhereWindowsShipsTheModule()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        InstallModule();

        Assert.True(await provider.IsPresentAsync());
    }

    /// <summary>
    /// §5.1: the plan is Windows' command run by Windows PowerShell from the Windows directory, and the
    /// estimate is the figure Windows reported rather than anything Deguffer measured.
    /// </summary>
    [Fact]
    public async Task ItPlansWindowsOwnCommandSizedByWindowsOwnFigure()
    {
        InstallModule();
        ReportCache(CacheBytes);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Path.Combine(PowerShellHome, "powershell.exe"), step.FileName);
        Assert.Contains("Delete-DeliveryOptimizationCache -Force", step.Arguments);
        Assert.Equal(CacheBytes, step.EstimatedBytes);
        Assert.False(step.Estimated.IsApproximate);
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);

        // The measurement went to the same host, and nothing was cleared while planning.
        var asked = Assert.Single(_runner.Invocations);
        Assert.Equal(step.FileName, asked.FileName);
        Assert.Contains("Get-DeliveryOptimizationPerfSnap", asked.Arguments);
    }

    /// <summary>
    /// A pinned file is one Delivery Optimization was told to keep. Overriding that is Deguffer's
    /// judgement in place of the service's, so the switch that does it must never be passed.
    /// </summary>
    [Fact]
    public async Task ItNeverAsksWindowsToRemovePinnedFiles()
    {
        InstallModule();
        ReportCache(CacheBytes);

        var provider = CreateProvider();
        await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.DoesNotContain(
            _runner.Invocations,
            call => call.Arguments.Contains("IncludePinnedFiles", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            _runner.Invocations,
            call => call.Arguments.Contains("Delete-DeliveryOptimizationCache", StringComparison.Ordinal));
    }

    /// <summary>
    /// The common case, since the cache limits itself and a machine set to download from Microsoft
    /// only keeps nothing. Windows' zero is exact, so the row is clear rather than unexamined.
    /// </summary>
    [Fact]
    public async Task AnEmptyCacheIsClearAndOffersNothing()
    {
        InstallModule();
        ReportCache(0);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// A failed query is not an empty cache. Reading it as one would call a cache of any size clear.
    /// </summary>
    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "")]
    [InlineData(0, "-1")]
    [InlineData(0, "5,368,709,120")]
    public async Task AQueryWindowsDidNotAnswerOffersNothingAndIsNotClear(int exitCode, string output)
    {
        InstallModule();
        _runner.Responding("Get-DeliveryOptimizationPerfSnap", output, exitCode);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task WithoutTheModuleNothingIsAskedOrOffered()
    {
        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>
    /// What the clean freed is Windows' figure before less Windows' figure after. The folder cannot
    /// be measured, so without asking again the run would report the whole estimate as freed.
    /// </summary>
    [Fact]
    public async Task TheReclaimIsWindowsOwnFigureAskedAgainAfterTheCommand()
    {
        InstallModule();

        const long remaining = 1L * 1024 * 1024 * 1024;
        var cleared = false;

        _runner.Replying(arguments =>
        {
            if (arguments.Contains("Delete-DeliveryOptimizationCache", StringComparison.Ordinal))
            {
                cleared = true;
                return new CommandOutcome(0, "Successfully deleted Delivery Optimization cache", string.Empty);
            }

            var bytes = cleared ? remaining : CacheBytes;
            return new CommandOutcome(0, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty);
        });

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.True(step.Succeeded);
        Assert.Equal(CacheBytes - remaining, step.BytesReclaimed);
    }

    /// <summary>
    /// Windows cleared the cache and would not say what it holds afterwards. Nothing is counted,
    /// because the whole estimate would be a figure nobody checked.
    /// </summary>
    [Fact]
    public async Task ARunWindowsWouldNotMeasureAfterwardsCountsNothing()
    {
        InstallModule();

        var asked = 0;

        _runner.Replying(arguments =>
            arguments.Contains("Delete-DeliveryOptimizationCache", StringComparison.Ordinal)
                ? new CommandOutcome(0, "Successfully deleted Delivery Optimization cache", string.Empty)
                : ++asked == 1
                    ? new CommandOutcome(0, CacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty)
                    : new CommandOutcome(1, string.Empty, "The service did not answer."));

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Contains("nothing is counted as reclaimed", step.Message);
    }

    /// <summary>
    /// §5.2 and §5.6: the plan names no path to delete, protects Windows Update's folder and its
    /// history by name, and a run proves that they and the unrecognised folder beside them stand.
    /// </summary>
    [Fact]
    public async Task TheRunTargetsNoPathAndWindowsUpdateSurvivesIt()
    {
        InstallModule();
        ReportCache(CacheBytes);
        var (softwareDistribution, dataStore, download) = CreateSoftwareDistribution();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps)).MeasuredPaths);
        Assert.All(
            [softwareDistribution, dataStore],
            path => Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.HeldContentBefore));

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(Path.Combine(dataStore, "DataStore.edb")));
        Assert.True(File.Exists(Path.Combine(download, "pending.cab")));
        Assert.True(File.Exists(Path.Combine(softwareDistribution, "ReportingEvents.log")));
    }

    /// <summary>
    /// The negative is live: a command that reached Windows Update's history would fail the run
    /// rather than pass it.
    /// </summary>
    [Fact]
    public async Task ACommandThatReachedTheUpdateHistoryFailsTheNegative()
    {
        InstallModule();
        var (_, dataStore, _) = CreateSoftwareDistribution();

        _runner.Replying(arguments =>
        {
            if (arguments.Contains("Delete-DeliveryOptimizationCache", StringComparison.Ordinal))
            {
                Directory.Delete(dataStore, recursive: true);
            }

            return new CommandOutcome(0, CacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty);
        });

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(
            result.Verification.Failures,
            c => c.Subject.Equals(dataStore, StringComparison.OrdinalIgnoreCase));
    }
}
