using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    /// <summary>The network adapter class, which a staged package of a test is filed under.</summary>
    internal const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly string _store;

    /// <summary>
    /// A measurement Windows' cleanup never finishes on its own, so only stopping it ends a survey.
    /// Completed when the test ends, so a survey a defect left running does not hold its thread.
    /// </summary>
    private readonly TaskCompletionSource _endless = new();

    public DriverStoreProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
        _store = Path.Combine(_system.WindowsDirectory, "System32", "DriverStore", "FileRepository");
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        _endless.TrySetResult();
        _temp.Dispose();
    }

    /// <summary>
    /// Windows' cleanup measuring until it is told to stop, and then answering "has something", as the
    /// real one does. A plan that read a survey it stopped would offer packages from that answer.
    /// </summary>
    private FakeDiskCleanupHandlers StillMeasuring() => FakeDiskCleanupHandlers.Measuring(_endless.Task);

    /// <summary>Long enough that only a plan waiting on something that never comes reaches it.</summary>
    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A listing that returns only once Windows' cleanup is measuring, so the plan decides whether it
    /// needs the answer while the handler is busy. A plan can otherwise stop the survey before its
    /// thread reaches the handler, and a test could not tell a stopped survey from one never asked.
    /// </summary>
    private static FakeDriverStore ListedWhileMeasuring(FakeDiskCleanupHandlers handlers, DriverStoreListing listing) =>
        new(async ct =>
        {
            await handlers.SurveyStarted.WaitAsync(Hung, ct);
            return listing;
        });

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
        FakeWindowsServicing? servicing = null,
        FakeDirectoryScanner? scanner = null) =>
        new(
            handlers ?? new FakeDiskCleanupHandlers((_, _) => new DiskCleanupOutcome(Ran: true)),
            _environment,
            runner ?? new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner,
            system: _system,
            servicing: servicing ?? FakeWindowsServicing.Settled,
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

    /// <summary>
    /// <c>pnputil</c>, removing the folder of each package it is named and <paramref name="alsoRemoving"/>.
    /// It answers only where Windows keeps it, so a step that named any other program fails.
    /// </summary>
    private FakeProcessRunner PnpUtil(IReadOnlyList<DriverPackage> packages, params string[] alsoRemoving) =>
        new FakeProcessRunner().Replying(DriverStore.PnpUtil(_system), arguments =>
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
        Assert.Equal([oldest.Folder!, older.Folder!], step.Destroys);
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

    /// <summary>
    /// §5.6 on Windows' cleanup: a package a device is using is checked afterwards whether or not it
    /// has an older version, because Windows never removes one.
    /// </summary>
    [Fact]
    public async Task ACleanupThatTakesAPackageADeviceIsUsingFailsVerification()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var used = Staged("oem3.inf", "2023-01-01", original: "bt.inf", inUse: true);
        var provider = CreateProvider(new FakeDriverStore(older, newest, used), Removing(older.Folder!, used.Folder!));

        var plan = await provider.PlanAsync();
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(used.Folder, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(used.Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A package Windows would not name a folder for leaves a folder the listing cannot account for, so
    /// no unlisted folder is called Windows' own, and Windows' cleanup taking that package's folder is
    /// not reported as an over-reach.
    /// </summary>
    [Fact]
    public async Task CallsNoFolderWindowsOwnWhereAPackageCouldNotBeMatchedToItsFolder()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var unmatched = Staged("oem3.inf", "2021-01-01");
        var provider = CreateProvider(
            new FakeDriverStore(older, newest, unmatched with { Folder = null }),
            Removing(older.Folder!, unmatched.Folder!));

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(unmatched.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.TargetedPaths, p => p.Equals(unmatched.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not match every driver package", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(Survived(result, newest.Folder!));
    }

    [Fact]
    public async Task NeverOffersAnOlderPackageADeviceIsUsing()
    {
        var older = Staged("oem1.inf", "2022-01-01", inUse: true);
        var newest = Staged("oem2.inf", "2024-01-01");
        var handlers = StillMeasuring();
        var provider = CreateProvider(ListedWhileMeasuring(handlers, new DriverStoreListing([older, newest])), handlers);

        var plan = await provider.PlanAsync().WaitAsync(Hung);

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("a device is installed with", StringComparison.Ordinal));

        // Asked beside the listing, then stopped unread once the listing showed nothing superseded.
        Assert.Equal([(DriverStoreProvider.Handler, true)], handlers.Returned);
    }

    [Fact]
    public async Task IsNotPresentWhereNoDriverHasAnOlderVersion()
    {
        var handlers = StillMeasuring();
        var provider = CreateProvider(
            ListedWhileMeasuring(
                handlers,
                new DriverStoreListing([Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01", original: "bt.inf")])),
            handlers);

        Assert.False(await provider.IsPresentAsync().WaitAsync(Hung));

        // The planner plans no row that is absent, so presence stops the survey it started, and waits
        // for Windows' cleanup to return rather than leaving it running past the pass.
        Assert.Equal([(DriverStoreProvider.Handler, true)], handlers.Returned);

        var plan = await provider.PlanAsync().WaitAsync(Hung);
        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>A listing that failed is no evidence that nothing is older, so the row appears and says why.</summary>
    [Fact]
    public async Task AListingThatFailedIsShownAndOffersNothing()
    {
        var handlers = StillMeasuring();
        var provider = CreateProvider(ListedWhileMeasuring(handlers, DriverStoreListing.Failed("pnputil would not start.")), handlers);

        Assert.True(await provider.IsPresentAsync().WaitAsync(Hung));

        var plan = await provider.PlanAsync().WaitAsync(Hung);
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("pnputil would not start.", StringComparison.Ordinal));
        Assert.Equal([(DriverStoreProvider.Handler, true)], handlers.Returned);
    }

    /// <summary>
    /// Windows' cleanup is asked what it would clear while pnputil lists the store, not after it: the
    /// two took 1.2 to 2.1 s and 3.5 to 4.1 s elevated, and together as long as the survey alone. The
    /// listing here finishes only once the survey has started, and the survey only once the listing
    /// has, so a plan that asked one after the other never finishes.
    /// </summary>
    [Fact]
    public async Task AsksWindowsCleanupWhileTheStoreIsBeingListed()
    {
        DriverPackage[] packages = [Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01")];
        var listing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = FakeDiskCleanupHandlers.Measuring(listing.Task);
        var store = new FakeDriverStore(async ct =>
        {
            listing.TrySetResult();
            await handlers.SurveyStarted.WaitAsync(Hung, ct);
            return new DriverStoreListing(packages);
        });
        var provider = CreateProvider(store, handlers);

        Assert.True(await provider.IsPresentAsync().WaitAsync(Hung));
        var plan = await provider.PlanAsync().WaitAsync(Hung);

        var step = Assert.IsType<DiskCleanupStep>(Assert.Single(plan.Steps));
        Assert.Equal(DriverStoreProvider.Handler, step.Handler);
        Assert.Equal([(DriverStoreProvider.Handler, false)], handlers.Returned);
        Assert.Equal(1, store.Listings);
    }

    /// <summary>
    /// A pass that found the row present and ended before planning it, as a cancelled preview does:
    /// the next pass's invalidation stops the survey that pass started rather than leaving Windows'
    /// cleanup measuring beside whatever runs next.
    /// </summary>
    [Fact]
    public async Task InvalidationStopsASurveyNoPlanTook()
    {
        var handlers = StillMeasuring();
        var provider = CreateProvider(
            ListedWhileMeasuring(handlers, new DriverStoreListing([Staged("oem1.inf", "2022-01-01"), Staged("oem2.inf", "2024-01-01")])),
            handlers);
        Assert.True(await provider.IsPresentAsync().WaitAsync(Hung));

        provider.InvalidateCaches();

        await handlers.SurveyReturned.WaitAsync(Hung);
        Assert.Equal([(DriverStoreProvider.Handler, true)], handlers.Returned);
    }

    /// <summary>
    /// Windows' cleanup failing to load or to measure is no reason to lose a plan that did not need its
    /// answer: before the survey started early, these plans never asked, and the failure would take
    /// the whole preview with it.
    /// </summary>
    [Fact]
    public async Task AHandlerThatFailsDoesNotFailAPlanThatDidNotNeedItsAnswer()
    {
        var older = Staged("oem1.inf", "2022-01-01", inUse: true);
        var handlers = FakeDiskCleanupHandlers.FailingOnceStopped(new InvalidCastException("Not an IEmptyVolumeCache."));
        var provider = CreateProvider(ListedWhileMeasuring(handlers, new DriverStoreListing([older, Staged("oem2.inf", "2024-01-01")])), handlers);

        var plan = await provider.PlanAsync().WaitAsync(Hung);

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #691: a pass is planned against one reading of what Windows servicing was doing, taken before
    /// anything in the pass could change it. An update that starts while the store is listed is not
    /// seen by the preview, which offers what the survey found, and every package step is held by the
    /// clean, which asks the machine itself.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatStartsWhileTheStoreIsListedIsHeldByTheClean()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        DriverPackage[] packages = [older, Staged("oem2.inf", "2024-01-01")];
        var servicing = new FakeWindowsServicing();
        var handlers = Removing(older.Folder!);
        var store = new FakeDriverStore(_ =>
        {
            servicing.IsRestartPending = true;
            return Task.FromResult(new DriverStoreListing(packages));
        });
        var provider = CreateProvider(store, handlers, servicing: servicing);

        var plan = await provider.PlanAsync().WaitAsync(Hung);
        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.True(step.HeldWhileUpdating));

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(Directory.Exists(older.Folder));
        Assert.All(result.Steps, step => Assert.Contains("waiting for a restart", step.Message, StringComparison.Ordinal));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// An update unfinished as the pass began holds the whole pass, even where it finishes while the
    /// store is listed, and Windows' cleanup is not asked. The next pass reads the machine afresh, and
    /// asks it.
    /// </summary>
    [Fact]
    public async Task AnUpdateUnfinishedAsThePassBeganHoldsThePassAndTheNextPassAsks()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        DriverPackage[] packages = [older, Staged("oem2.inf", "2024-01-01")];
        var servicing = new FakeWindowsServicing { IsRestartPending = true };
        var handlers = FakeDiskCleanupHandlers.FindingNothing();
        var store = new FakeDriverStore(_ =>
        {
            servicing.IsRestartPending = false;
            return Task.FromResult(new DriverStoreListing(packages));
        });
        var provider = CreateProvider(store, handlers, servicing: servicing);

        var held = await provider.PlanAsync().WaitAsync(Hung);

        Assert.Empty(held.Steps);
        Assert.True(held.WaitsForAnUpdate);
        Assert.Contains(held.ProtectedPaths, p =>
            p.Path.Equals(older.Folder, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
        Assert.Empty(handlers.Returned);

        provider.InvalidateCaches();
        var next = await provider.PlanAsync().WaitAsync(Hung);

        Assert.False(next.WaitsForAnUpdate);
        Assert.Contains(next.Notes, n => n.Message.Contains("finds nothing of its own to clear", StringComparison.Ordinal));
        Assert.Equal([(DriverStoreProvider.Handler, false)], handlers.Returned);
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

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        var oldest = Staged("oem1.inf", "2021-01-01");
        var older = Staged("oem2.inf", "2022-01-01");
        var newest = Staged("oem3.inf", "2024-01-01");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(new FakeDriverStore(oldest, older, newest))));
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
            Assert.True(step.HeldWhileUpdating);
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

    /// <summary>
    /// Windows gives a freed <c>oem&lt;N&gt;.inf</c> name to the next package it stages. A name that has
    /// changed hands since the preview stops the removal, and the package now holding it is untouched.
    /// </summary>
    [Fact]
    public async Task DoesNotRunPnpUtilOnANameThatNowBelongsToAnotherPackage()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var store = new FakeDriverStore(older, newest);
        var runner = PnpUtil([older]);
        var provider = CreateProvider(store, FakeDiskCleanupHandlers.NoneRegistered(), runner);

        var plan = await provider.PlanAsync();
        var arrived = Staged("oem9.inf", "2025-01-01", original: "gpu.inf");
        store.Rename("oem1.inf", arrived.Folder);

        var result = await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("/delete-driver", StringComparison.Ordinal));
        Assert.True(Directory.Exists(arrived.Folder));
        Assert.True(Directory.Exists(older.Folder));
        Assert.Contains(result.Steps, s => !s.Succeeded && s.Message!.Contains("different driver package", StringComparison.Ordinal));
    }

    /// <summary>A package removed since the preview leaves its name unused, and nothing is asked of pnputil.</summary>
    [Fact]
    public async Task DoesNotRunPnpUtilOnANameNoPackageHoldsAnyMore()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var store = new FakeDriverStore(older, Staged("oem2.inf", "2024-01-01"));
        var runner = PnpUtil([older]);
        var provider = CreateProvider(store, FakeDiskCleanupHandlers.NoneRegistered(), runner);

        var plan = await provider.PlanAsync();
        store.Rename("oem1.inf", null);

        var result = await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("/delete-driver", StringComparison.Ordinal));
        Assert.Contains(result.Steps, s => !s.Succeeded && s.Message!.Contains("no longer has", StringComparison.Ordinal));
    }

    /// <summary>
    /// On the pnputil route Deguffer names exactly what goes, so a package it could not match to its
    /// folder changes nothing: every folder the listing does not account for is still checked afterwards.
    /// </summary>
    [Fact]
    public async Task PnpUtilTakingAnUnlistedFolderFailsVerificationEvenWhereAPackageWasUnmatched()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var unmatched = Staged("oem3.inf", "2020-01-01", original: "bt.inf");
        var windows = PartOfWindows();
        var provider = CreateProvider(
            new FakeDriverStore(older, newest, unmatched with { Folder = null }),
            FakeDiskCleanupHandlers.NoneRegistered(),
            PnpUtil([older], windows));

        var plan = await provider.PlanAsync();
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(windows, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unmatched.Folder, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(windows, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// An update that starts between the preview and the clean holds pnputil back as it holds Windows'
    /// own cleanup, because the hold at planning has already been passed.
    /// </summary>
    [Fact]
    public async Task AnUpdateStartedSinceThePreviewStopsPnpUtil()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var servicing = FakeWindowsServicing.Settled;
        var runner = PnpUtil([older]);
        var provider = CreateProvider(
            new FakeDriverStore(older, newest), FakeDiskCleanupHandlers.NoneRegistered(), runner, servicing);

        var plan = await provider.PlanAsync();
        Assert.Single(plan.Steps);

        servicing.IsRestartPending = true;
        var result = await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("/delete-driver", StringComparison.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(older.Folder));
        Assert.Contains("restart", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
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
        var handlers = new FakeDiskCleanupHandlers((_, _) => new DiskCleanupOutcome(Ran: true));
        var provider = CreateProvider(store, handlers);

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        Assert.Equal(1, store.Listings);
        Assert.Equal([(DriverStoreProvider.Handler, DiskCleanupQuestion.Measures)], handlers.Surveyed);

        provider.InvalidateCaches();
        await provider.PlanAsync();
        Assert.Equal(2, store.Listings);
        Assert.Equal(2, handlers.Surveyed.Count);
    }

    /// <summary>
    /// §9 under the store's own reach: the driver store is inside the Windows directory, so the run
    /// shows afterwards that <c>WinSxS</c> and <c>Installer</c> stood, and a cleanup that took either
    /// fails it however well it did the rest.
    /// </summary>
    [Theory]
    [InlineData("WinSxS")]
    [InlineData("Installer")]
    public async Task ACleanupThatReachesASection9ExclusionFailsVerification(string folder)
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        _temp.CreateFile(4096, "Windows", "WinSxS", "component.manifest");
        _temp.CreateFile(4096, "Windows", "Installer", "patch.msp");
        var reached = Path.Combine(_system.WindowsDirectory, folder);
        var provider = CreateProvider(new FakeDriverStore(older, newest), Removing(older.Folder!, reached));

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.All(
            [Path.Combine(_system.WindowsDirectory, "WinSxS"), Path.Combine(_system.WindowsDirectory, "Installer")],
            path => Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.HeldContentBefore));
        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(reached, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6's record of a package folder comes from the row's own listing of the store, not from asking
    /// each of several hundred folders again (G5). A package pnputil is not asked about, removed after
    /// the listing and before the plan protected it, is recorded as there, which is what the listing saw
    /// when the plan was made, and the check after the clean reports it gone rather than passing over it
    /// as never there.
    /// </summary>
    [Fact]
    public async Task APackageFolderIsRecordedAsTheListingSawItAndItsLossIsStillReported()
    {
        var older = Staged("oem1.inf", "2022-01-01");
        var newest = Staged("oem2.inf", "2024-01-01");
        var alone = Staged("oem3.inf", "2020-01-01", original: "bt.inf");
        var provider = CreateProvider(
            new FakeDriverStore(older, newest, alone),
            FakeDiskCleanupHandlers.NoneRegistered(),
            PnpUtil([older]),
            scanner: FakeDirectoryScanner.RemovingOnFirstMeasure(alone.Folder!));

        var plan = await provider.PlanAsync();

        Assert.False(Directory.Exists(alone.Folder));
        Assert.Equal(
            PathPresence.Present,
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(alone.Folder, StringComparison.OrdinalIgnoreCase)).PresenceBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(
            VerificationOutcome.RemovedFromOutside,
            Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(alone.Folder, StringComparison.OrdinalIgnoreCase)).Outcome);
    }
}
