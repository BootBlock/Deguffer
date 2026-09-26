using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Older versions of third-party drivers in Windows' driver store, removed by Windows' own cleanup and,
/// where that cannot be loaded, by <c>pnputil</c>.
///
/// <para>The store is a scratch tree under <see cref="FakeSystemDirectories"/>, its listing is
/// <see cref="FakeDriverStore"/>, and the handler and <c>pnputil</c> are fakes that remove what the
/// test says Windows would. The real ones would remove drivers from whoever runs the suite.</para>
/// </summary>
public sealed class DriverStoreProviderTests : IDisposable
{
    private const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly string _store;

    public DriverStoreProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
        _store = Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository");
        Directory.CreateDirectory(_store);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A third-party package staged in the store, holding one file.</summary>
    private DriverPackage Staged(string published, string date, string original = "wifi.inf", bool inUse = false)
    {
        var folder = Path.Combine(_store, $"{original}_amd64_{Path.GetFileNameWithoutExtension(published)}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, original), new byte[4096]);

        return new DriverPackage(
            published,
            original,
            "Example Networks",
            NetClass,
            ExtensionId: null,
            DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            new Version(1, 0, 0, 0),
            inUse,
            folder);
    }

    /// <summary>A driver that is part of Windows: in the store, and not in pnputil's listing.</summary>
    private string PartOfWindows(string name = "netwtw.inf_amd64_0")
    {
        var folder = Path.Combine(_store, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "netwtw.inf"), new byte[64]);
        return folder;
    }

    private DriverStoreProvider CreateProvider(
        FakeDriverStore store,
        FakeDiskCleanupHandlers? handlers = null,
        FakeProcessRunner? runner = null,
        FakeWindowsServicing? servicing = null) =>
        new(
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            servicing: servicing ?? FakeWindowsServicing.Settled,
            handlers: handlers ?? new FakeDiskCleanupHandlers((_, _) => new DiskCleanupOutcome(Ran: true)),
            store: store);

    /// <summary>Windows' cleanup, taking <paramref name="folders"/> as the real one takes what it judges superseded.</summary>
    private static FakeDiskCleanupHandlers Removing(params string[] folders) => new((_, _) =>
    {
        foreach (var folder in folders)
        {
            Directory.Delete(LongPath.Extended(folder), recursive: true);
        }

        return new DiskCleanupOutcome(Ran: true);
    });

    /// <summary><c>pnputil</c>, removing the folder of each package it is named and <paramref name="alsoRemoving"/>.</summary>
    private static FakeProcessRunner PnpUtil(IReadOnlyList<DriverPackage> packages, params string[] alsoRemoving) =>
        new FakeProcessRunner().Replying(arguments =>
        {
            var named = packages.Single(p => arguments.EndsWith(p.PublishedName, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(LongPath.Extended(named.Folder!), recursive: true);

            foreach (var folder in alsoRemoving)
            {
                Directory.Delete(LongPath.Extended(folder), recursive: true);
            }

            return new CommandOutcome(0, $"Driver package deleted successfully.", string.Empty);
        });

    private static bool Survived(CleanupResult result, string path) =>
        result.Verification!.Checks.Any(c =>
            c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Outcome == VerificationOutcome.Survived);

    [Fact]
    public void IsTierTwoAndNamedForWhatItOffers()
    {
        var provider = CreateProvider(new FakeDriverStore());

        Assert.Equal("driver-store", provider.Id);
        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.Equal(_store, provider.Repository);
    }

    /// <summary>
    /// §5.1: one step of Windows' own cleanup, naming every older package, and never the store itself.
    /// </summary>
    [Fact]
    public async Task OffersTheOlderPackagesAsOneStepOfWindowsOwnCleanup()
    {
        var oldest = Staged("oem1.inf", "2021-01-01");
        var older = Staged("oem2.inf", "2022-01-01");
        var newest = Staged("oem3.inf", "2024-01-01");
        var provider = CreateProvider(new FakeDriverStore(oldest, older, newest));

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        var step = Assert.IsType<DiskCleanupStep>(Assert.Single(plan.Steps));
        Assert.Equal(DriverStoreProvider.Handler, step.Handler);
        Assert.Equal([oldest.Folder, older.Folder], step.Destroys);
        Assert.DoesNotContain(plan.TargetedPaths, p => p.Equals(_store, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.TargetedPaths, p => p.Equals(newest.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(step.RequiresElevation);
        Assert.True(step.HeldWhileUpdating);
        Assert.True(step.EstimatedBytes >= 2 * 4096);
    }

    /// <summary>
    /// §6.3 at this boundary is the display form: the handler takes <c>C:\</c>, never <c>\\?\C:\</c>.
    /// §5.6 afterwards: the store, the newest version and a driver that is part of Windows all stand.
    /// </summary>
    [Fact]
    public async Task RunsWindowsCleanupOnTheSystemDriveAndEverythingElseInTheStoreSurvives()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var windows = PartOfWindows();
        var handlers = Removing(older.Folder!);
        var provider = CreateProvider(new FakeDriverStore(older, newest), handlers);

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        var (handler, volume) = Assert.Single(handlers.Calls);
        Assert.Equal(DriverStoreProvider.Handler, handler);
        Assert.Equal(LongPath.Display(_system.SystemDrive), volume);
        Assert.DoesNotContain(@"\\?\", volume, StringComparison.Ordinal);

        Assert.False(Directory.Exists(older.Folder));
        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(Survived(result, _store));
        Assert.True(Survived(result, newest.Folder!));
        Assert.True(Survived(result, windows));
    }

    /// <summary>§5.6: Windows' cleanup taking the newest version fails the run, however well it did the rest.</summary>
    [Fact]
    public async Task ACleanupThatTakesTheNewestVersionFailsVerification()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var provider = CreateProvider(new FakeDriverStore(older, newest), Removing(older.Folder!, newest.Folder!));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(newest.Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.2 and §5.6: a driver that is part of Windows is never offered and must survive.</summary>
    [Fact]
    public async Task ACleanupThatTakesADriverThatIsPartOfWindowsFailsVerification()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var windows = PartOfWindows();
        var provider = CreateProvider(new FakeDriverStore(older, newest), Removing(older.Folder!, windows));

        var plan = await provider.PlanAsync();
        Assert.DoesNotContain(plan.TargetedPaths, p => p.Equals(windows, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(windows, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverOffersAnOlderPackageADeviceIsUsing()
    {
        var older = Staged("oem1.inf", "2022-01-01", inUse: true);
        var newest = Staged("oem2.inf", "2024-01-01");
        var handlers = Removing();
        var provider = CreateProvider(new FakeDriverStore(older, newest), handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("a device is installed with", StringComparison.Ordinal));
        Assert.Empty(handlers.Surveyed);
    }

    [Fact]
    public async Task IsNotPresentWhereNoDriverHasAnOlderVersion()
    {
        var provider = CreateProvider(new FakeDriverStore(Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01", original: "bt.inf")));

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>A listing that failed is no evidence that nothing is older, so the row appears and says why.</summary>
    [Fact]
    public async Task AListingThatFailedIsShownAndOffersNothing()
    {
        var provider = CreateProvider(new FakeDriverStore(() => DriverStoreListing.Failed("pnputil would not start.")));

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("pnputil would not start.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Unelevated, Windows' cleanup cannot be asked, so the row is offered as needing administrator
    /// rights and the plan made after elevating asks.
    /// </summary>
    [Fact]
    public async Task OffersTheRowAsNeedingElevationWhereTheCleanupCannotBeAsked()
    {
        var provider = CreateProvider(
            new FakeDriverStore(Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01")),
            FakeDiskCleanupHandlers.Unasked());

        var plan = await provider.PlanAsync();

        Assert.True(Assert.IsType<DiskCleanupStep>(Assert.Single(plan.Steps)).RequiresElevation);
    }

    /// <summary>§5.1: where Windows' cleanup finds nothing of its own, Deguffer does not overrule it.</summary>
    [Fact]
    public async Task LeavesThePackagesAloneWhereWindowsCleanupFindsNothingToClear()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var provider = CreateProvider(
            new FakeDriverStore(older, Staged("oem2.inf", "2024-01-01")),
            FakeDiskCleanupHandlers.FindingNothing());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where Windows' cleanup cannot be loaded, each older package is removed with pnputil, named by the
    /// name Windows gave it, and never with <c>/force</c> or <c>/uninstall</c>.
    /// </summary>
    [Fact]
    public async Task FallsBackToPnpUtilOnePackageAtATimeWhereTheCleanupIsNotAvailable()
    {
        var oldest = Staged("oem1.inf", "2021-01-01");
        var older = Staged("oem2.inf", "2022-01-01");
        var newest = Staged("oem3.inf", "2024-01-01");
        var alone = Staged("oem4.inf", "2020-01-01", original: "bt.inf");
        var windows = PartOfWindows();
        var runner = PnpUtil([oldest, older]);
        var provider = CreateProvider(new FakeDriverStore(oldest, older, newest, alone), FakeDiskCleanupHandlers.NoneRegistered(), runner);

        var plan = await provider.PlanAsync();

        var steps = plan.Steps.Cast<RunCommandStep>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step =>
        {
            Assert.Equal(Path.Combine(_system.WindowsDirectory, "System32", "pnputil.exe"), step.FileName);
            Assert.DoesNotContain("/force", step.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/uninstall", step.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.True(step.RequiresElevation);
        });
        Assert.Equal(["/delete-driver oem1.inf", "/delete-driver oem2.inf"], steps.Select(s => s.Arguments));
        Assert.Equal([oldest.Folder, older.Folder], steps.Select(s => s.Removes));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(Survived(result, _store));
        Assert.True(Survived(result, newest.Folder!));
        Assert.True(Survived(result, alone.Folder!));
        Assert.True(Survived(result, windows));
    }

    /// <summary>§5.6 on the pnputil route: Deguffer names exactly what goes, so any other package taken fails the run.</summary>
    [Fact]
    public async Task PnpUtilTakingAPackageItWasNotNamedFailsVerification()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var alone = Staged("oem3.inf", "2020-01-01", original: "bt.inf");
        var provider = CreateProvider(
            new FakeDriverStore(older, newest, alone),
            FakeDiskCleanupHandlers.NoneRegistered(),
            PnpUtil([older], alone.Folder!));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(alone.Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Windows Update installs drivers too, so nothing is offered while an update is unfinished.</summary>
    [Fact]
    public async Task HoldsEverythingBackWhileAnUpdateIsUnfinished()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var handlers = Removing();
        var provider = CreateProvider(
            new FakeDriverStore(older, Staged("oem2.inf", "2024-01-01")),
            handlers,
            servicing: new FakeWindowsServicing { IsRestartPending = true });

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
        Assert.Empty(handlers.Surveyed);
    }

    /// <summary>
    /// Windows' cleanup takes every package it judges superseded at once, so a guard on recently changed
    /// files that would keep any of them withdraws the whole step rather than letting it take more.
    /// </summary>
    [Fact]
    public async Task WithdrawsTheCleanupWhereTheGuardWouldKeepAnyOfIt()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var provider = CreateProvider(new FakeDriverStore(older, Staged("oem2.inf", "2024-01-01")));

        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow));

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
    }

    /// <summary>G4: presence and planning read one listing per pass, and the next pass reads afresh.</summary>
    [Fact]
    public async Task ListsTheStoreOncePerPlanningPass()
    {
        var store = new FakeDriverStore(Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01"));
        var provider = CreateProvider(store);

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        Assert.Equal(1, store.Listings);

        provider.InvalidateCaches();
        await provider.PlanAsync();
        Assert.Equal(2, store.Listings);
    }
}
