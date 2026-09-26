using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The component store's two rows: DISM's own cleanup at Tier 2, and its reset at Tier 3. Neither ever
/// deletes a path.
///
/// <para>Everything runs against a synthetic Windows directory and a fake DISM. The real store can only
/// be analysed by an administrator, and a test that cleaned it would change the Windows of whoever ran
/// the suite.</para>
/// </summary>
public sealed class ComponentStoreProviderTests : IDisposable
{
    private const long Gigabyte = 1_000_000_000;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeWindowsServicing _servicing = FakeWindowsServicing.Settled;
    private readonly string _store;
    private readonly string _packages;
    private readonly string _system32;

    public ComponentStoreProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
        _temp.CreateFile(4096, "Windows", "WinSxS", "Manifests", "component.manifest");
        _temp.CreateFile(2048, "Windows", "servicing", "Packages", "Package_for_RollupFix.mum");
        _temp.CreateFile(1024, "Windows", "System32", "kernel32.dll");
        _store = Path.Combine(_system.WindowsDirectory, "WinSxS");
        _packages = Path.Combine(_system.WindowsDirectory, "servicing", "Packages");
        _system32 = Path.Combine(_system.WindowsDirectory, "System32");
    }

    public void Dispose() => _temp.Dispose();

    private ComponentStoreCleanupProvider Cleanup(ComponentStoreAnalysis? analysis = null) =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis);

    private ComponentStoreResetBaseProvider Reset(ComponentStoreAnalysis? analysis = null) =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis);

    private static bool IsAnalysis(string arguments) =>
        arguments.Contains("/AnalyzeComponentStore", StringComparison.Ordinal);

    private static bool IsCleanup(string arguments) =>
        arguments.Contains("/StartComponentCleanup", StringComparison.Ordinal);

    /// <summary>DISM's English report with the figures a test cares about put into it.</summary>
    private static string Report(long actual, long backups = 3 * Gigabyte, bool recommended = true) =>
        ComponentStoreReportTests.Sample
            .Replace("Actual Size of Component Store : 21.33 GB", $"Actual Size of Component Store : {Size(actual)}", StringComparison.Ordinal)
            .Replace("Backups and Disabled Features : 12.98 GB", $"Backups and Disabled Features : {Size(backups)}", StringComparison.Ordinal)
            .Replace("Recommended : Yes", recommended ? "Recommended : Yes" : "Recommended : No", StringComparison.Ordinal);

    private static string Size(long bytes) =>
        (bytes / (decimal)Gigabyte).ToString("0.00", CultureInfo.InvariantCulture) + " GB";

    /// <summary>
    /// DISM answering each analysis with the next of <paramref name="actualSizes"/>, and each cleanup
    /// with success after running <paramref name="onCleanup"/>.
    /// </summary>
    private void Analyses(Action<string>? onCleanup = null, params long[] actualSizes)
    {
        var next = 0;

        _runner.Replying(arguments =>
        {
            if (IsAnalysis(arguments))
            {
                return new CommandOutcome(0, Report(actualSizes[Math.Min(next++, actualSizes.Length - 1)]), string.Empty);
            }

            onCleanup?.Invoke(arguments);
            return new CommandOutcome(0, "The operation completed successfully.", string.Empty);
        });
    }

    [Fact]
    public async Task ItIsPresentWhereWindowsKeepsAComponentStore()
    {
        Assert.True(await Cleanup().IsPresentAsync());

        Directory.Delete(_store, recursive: true);

        Assert.False(await Cleanup().IsPresentAsync());
        Assert.Empty((await Cleanup().PlanAsync()).Steps);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>
    /// §5.1: the plan is DISM's own cleanup, run from the Windows directory, elevated, held while Windows
    /// updates, and sized by the overhead Windows reports rather than by anything Deguffer measured.
    /// </summary>
    [Fact]
    public async Task ItPlansDismsOwnCleanupSizedByTheOverheadWindowsReports()
    {
        Analyses(null, 21 * Gigabyte);

        var plan = await Cleanup().PlanAsync();

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(_system.WindowsDirectory, Path.GetDirectoryName(Path.GetDirectoryName(step.FileName)));
        Assert.Equal("Dism.exe", Path.GetFileName(step.FileName));
        Assert.Equal("/Online /English /Cleanup-Image /StartComponentCleanup", step.Arguments);
        Assert.Equal(3 * Gigabyte, step.EstimatedBytes);
        Assert.True(step.Estimated.IsCeiling);
        Assert.Equal($"up to {FreeSpace.Format(3 * Gigabyte)}", FreeSpace.Format(step.Estimated));
        Assert.True(step.RequiresElevation);
        Assert.True(step.HeldWhileUpdating);
        Assert.IsType<ComponentStoreAnalysis>(step.MeasuredBy);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        // Only the analysis ran while planning, and it went to the same DISM.
        var asked = Assert.Single(_runner.Invocations);
        Assert.Equal(step.FileName, asked.FileName);
        Assert.Equal(ComponentStoreAnalysis.AnalyzeArguments, asked.Arguments);
    }

    /// <summary>
    /// The reset loses the means to uninstall every update installed so far, so it is never carried by the
    /// cleanup's row, and its own row is Tier 3.
    /// </summary>
    [Fact]
    public async Task TheResetIsItsOwnTier3RowAndTheCleanupNeverCarriesIt()
    {
        Analyses(null, 21 * Gigabyte);

        var cleanup = Assert.IsType<RunCommandStep>(Assert.Single((await Cleanup().PlanAsync()).Steps));
        var resetPlan = await Reset().PlanAsync();
        var reset = Assert.IsType<RunCommandStep>(Assert.Single(resetPlan.Steps));

        Assert.DoesNotContain("/ResetBase", cleanup.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/Online /English /Cleanup-Image /StartComponentCleanup /ResetBase", reset.Arguments);
        Assert.Equal(SafetyTier.UserData, resetPlan.Tier);
        Assert.NotEqual(Cleanup().Id, Reset().Id);
    }

    /// <summary>
    /// §7: the reset's confirmation says the loss is permanent and names it, and does not call it user
    /// data, which it is not.
    /// </summary>
    [Fact]
    public async Task TheResetsConfirmationNamesWhatIsLostWithoutCallingItUserData()
    {
        Analyses(null, 21 * Gigabyte);

        var requirement = ConfirmationRequirement.For(await Reset().PlanAsync());

        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Contains("cannot be undone", requirement.Consequence, StringComparison.Ordinal);
        Assert.Contains("No update installed so far can be uninstalled", requirement.Consequence, StringComparison.Ordinal);
        Assert.DoesNotContain("user data", requirement.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both rows offer the same overhead, so a clean of both confirms it once rather than promising
    /// twice what the store can give back.
    /// </summary>
    [Fact]
    public async Task BothRowsOfferOneOverheadAndAConfirmationOfBothCountsItOnce()
    {
        Analyses(null, 21 * Gigabyte);
        var analysis = new ComponentStoreAnalysis(_system, _runner);
        IReadOnlyList<CleanupPlan> plans = [await Cleanup(analysis).PlanAsync(), await Reset(analysis).PlanAsync()];

        Assert.All(plans, plan => Assert.Equal(ComponentStoreProviderBase.Overhead, Assert.Single(plan.Steps).SharesReclaim));
        Assert.Equal($"up to {FreeSpace.Format(3 * Gigabyte)}", CleanConfirmation.For(plans).TotalLabel);
    }

    /// <summary>G4: the analysis takes a minute or more, so both rows share one per planning pass.</summary>
    [Fact]
    public async Task BothRowsShareOneAnalysisPerPlanningPass()
    {
        Analyses(null, 21 * Gigabyte);
        var analysis = new ComponentStoreAnalysis(_system, _runner);
        var cleanup = Cleanup(analysis);
        var reset = Reset(analysis);

        await cleanup.PlanAsync();
        await reset.PlanAsync();

        Assert.Single(_runner.Invocations);

        cleanup.InvalidateCaches();
        reset.InvalidateCaches();
        await cleanup.PlanAsync();
        await reset.PlanAsync();

        Assert.Equal(2, _runner.Invocations.Count);
    }

    /// <summary>
    /// DISM answers only an administrator. A refusal is a reason to elevate, so the row says so and is
    /// not read as a store with nothing in it.
    /// </summary>
    [Fact]
    public async Task AnAccountDismRefusesIsNotExaminedAndIsToldToElevate()
    {
        _runner.Responding(
            "/AnalyzeComponentStore",
            "\r\nError: 740\r\n\r\nElevated permissions are required to run DISM.\r\nUse an elevated command prompt to complete these tasks.\r\n",
            ComponentStoreAnalysis.ElevationRequired);

        var plan = await Cleanup().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Scanning as administrator", StringComparison.Ordinal));
    }

    /// <summary>A failed or unreadable analysis is not an empty store, and says why without DISM's banner.</summary>
    [Theory]
    [InlineData(unchecked((int)0x800F0806), "Deployment Image Servicing and Management tool\r\n\r\nError: 0x800f0806\r\n\r\nThe operation could not be completed due to pending operations.\r\n", "Error: 0x800f0806 The operation could not be completed due to pending operations.")]
    [InlineData(0, "Component Store (WinSxS) information:\r\n", "did not include every figure")]
    public async Task AnAnalysisWindowsDidNotCompleteOffersNothingAndIsNotClear(int exitCode, string output, string reason)
    {
        _runner.Responding("/AnalyzeComponentStore", output, exitCode);

        var plan = await Cleanup().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(reason, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Deployment Image", StringComparison.Ordinal));
    }

    /// <summary>Windows' own verdict decides, and its "no" is exact, so the row is clear.</summary>
    [Fact]
    public async Task AStoreWindowsDoesNotRecommendCleaningIsClear()
    {
        _runner.Responding("/AnalyzeComponentStore", Report(21 * Gigabyte, recommended: false));

        var plan = await Cleanup().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>The arithmetic that answers Explorer's figure: most of the store is Windows itself.</summary>
    [Fact]
    public async Task ThePlanShowsWindowsOwnArithmetic()
    {
        Analyses(null, 21 * Gigabyte);

        var plan = await Cleanup().PlanAsync();

        var note = Assert.Single(plan.Notes, n => n.Message.StartsWith("Explorer counts", StringComparison.Ordinal)).Message;
        Assert.Contains(FreeSpace.Format(23_360_000_000), note, StringComparison.Ordinal);
        Assert.Contains(FreeSpace.Format(8_350_000_000), note, StringComparison.Ordinal);
        Assert.Contains(FreeSpace.Format(21 * Gigabyte), note, StringComparison.Ordinal);
        Assert.Contains("7 superseded packages", note, StringComparison.Ordinal);
        Assert.Contains("2026-09-25 06:30:36", note, StringComparison.Ordinal);
    }

    /// <summary>DISM is not even asked while Windows is in the middle of an update.</summary>
    [Fact]
    public async Task NothingIsAskedOrOfferedWhileWindowsIsUpdating()
    {
        Analyses(null, 21 * Gigabyte);
        _servicing.IsRestartPending = true;

        var plan = await Cleanup().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WaitsForAnUpdate);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>An update that starts while the preview is on screen holds the cleanup at the run.</summary>
    [Fact]
    public async Task ARunThatReachesAnUnfinishedUpdateDoesNotClean()
    {
        Analyses(null, 21 * Gigabyte);
        var provider = Cleanup();
        var plan = await provider.PlanAsync();

        _servicing.IsRestartPending = true;
        var result = await provider.ExecuteAsync(plan);

        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.DoesNotContain(_runner.Invocations, call => IsCleanup(call.Arguments));
    }

    /// <summary>
    /// The analysis before the command takes a minute or more, so an update that starts during it
    /// still holds the cleanup.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatStartsDuringTheAnalysisBeforeTheCommandHoldsTheCleanup()
    {
        var analyses = 0;
        _runner.Replying(arguments =>
        {
            if (!IsAnalysis(arguments))
            {
                return new CommandOutcome(0, "The operation completed successfully.", string.Empty);
            }

            if (++analyses == 2)
            {
                _servicing.IsRestartPending = true;
            }

            return new CommandOutcome(0, Report(21 * Gigabyte), string.Empty);
        });

        var provider = Cleanup();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal(2, analyses);
        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.DoesNotContain(_runner.Invocations, call => IsCleanup(call.Arguments));
    }

    /// <summary>
    /// The reclaim is Windows' before-and-after pair around the command. Windows' own scheduled cleanup
    /// shrank the store after the preview, and the plan's figure would credit Deguffer with that too.
    /// </summary>
    [Fact]
    public async Task TheReclaimIsWindowsOwnFigureImmediatelyBeforeAndAfterTheCommand()
    {
        Analyses(null, 21 * Gigabyte, 20 * Gigabyte, 18 * Gigabyte);

        var provider = Cleanup();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.True(step.Succeeded);
        Assert.Equal(2 * Gigabyte, step.BytesReclaimed);
        Assert.Equal(
            ["analyse", "analyse", "clean", "analyse"],
            _runner.Invocations.Select(call => IsAnalysis(call.Arguments) ? "analyse" : "clean"));
    }

    /// <summary>Both rows in one clean count each gigabyte once, although both were planned from one figure.</summary>
    [Fact]
    public async Task CleaningAndResettingInOneCleanCountEachReclaimOnce()
    {
        Analyses(null, 21 * Gigabyte, 21 * Gigabyte, 19 * Gigabyte, 19 * Gigabyte, 18 * Gigabyte);
        var analysis = new ComponentStoreAnalysis(_system, _runner);
        var cleanup = Cleanup(analysis);
        var reset = Reset(analysis);
        var cleanupPlan = await cleanup.PlanAsync();
        var resetPlan = await reset.PlanAsync();

        var cleaned = await cleanup.ExecuteAsync(cleanupPlan);
        var wasReset = await reset.ExecuteAsync(resetPlan);

        Assert.Equal(2 * Gigabyte, cleaned.BytesReclaimed);
        Assert.Equal(1 * Gigabyte, wasReset.BytesReclaimed);
    }

    /// <summary>
    /// The start figure is Windows' own immediately before the command, so a store that grew grew while
    /// the command ran, and the run says so rather than blaming the time since the scan.
    /// </summary>
    [Fact]
    public async Task AStoreThatGrewSaysItGrewWhileTheCommandRan()
    {
        Analyses(null, 21 * Gigabyte, 19 * Gigabyte, 20 * Gigabyte);

        var provider = Cleanup();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Contains("grew while the command ran", step.Message, StringComparison.Ordinal);
    }

    /// <summary>Windows would not say what the store held before the command, so nothing is counted.</summary>
    [Fact]
    public async Task ARunWindowsWouldNotMeasureBeforehandCountsNothing()
    {
        var analyses = 0;
        _runner.Replying(arguments => IsAnalysis(arguments)
            ? ++analyses == 2
                ? new CommandOutcome(unchecked((int)0x800F0806), "Error: 0x800f0806", string.Empty)
                : new CommandOutcome(0, Report(21 * Gigabyte - analyses * Gigabyte), string.Empty)
            : new CommandOutcome(0, "The operation completed successfully.", string.Empty));

        var provider = Cleanup();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Contains("before the command, so nothing is counted as reclaimed", step.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.2 and §5.6: no path is a target, and the store, Windows' record of its updates and Windows
    /// itself are protected on their contents as well as their existence.
    /// </summary>
    [Fact]
    public async Task TheRunTargetsNoPathAndProtectsTheStoreAndWindows()
    {
        Analyses(null, 21 * Gigabyte, 21 * Gigabyte, 19 * Gigabyte);

        var provider = Cleanup();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps)).MeasuredPaths);
        Assert.All(
            [_store, _packages, _system32],
            path => Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.HeldContentBefore));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>The negative is live: a command that emptied any of them fails the run.</summary>
    [Theory]
    [InlineData("WinSxS")]
    [InlineData("servicing")]
    [InlineData("System32")]
    public async Task ACommandThatEmptiedAProtectedFolderFailsTheNegative(string folder)
    {
        var emptied = folder == "servicing" ? _packages : Path.Combine(_system.WindowsDirectory, folder);

        Analyses(
            _ =>
            {
                foreach (var file in Directory.EnumerateFiles(emptied, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }

                foreach (var directory in Directory.EnumerateDirectories(emptied))
                {
                    Directory.Delete(directory, recursive: true);
                }
            },
            21 * Gigabyte);

        var provider = Reset();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(emptied, StringComparison.OrdinalIgnoreCase));
    }
}
