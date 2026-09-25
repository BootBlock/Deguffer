using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a finished Windows upgrade leaves at the top of the system drive, removed by Windows' own
/// Disk Cleanup handlers once the upgrade can no longer be undone.
///
/// <para>The system drive is a scratch tree (see <see cref="FakeSystemDirectories"/>) and the
/// handlers are <see cref="FakeDiskCleanupHandlers"/>, which clear what each registration names. The
/// real ones would remove the previous installation of whoever runs the suite.</para>
/// </summary>
public sealed class PreviousWindowsInstallationProviderTests : IDisposable
{
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromDays(20);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public PreviousWindowsInstallationProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Volume => _system.SystemDrive;

    private PreviousWindowsInstallationProvider CreateProvider(
        FakeDiskCleanupHandlers? handlers = null,
        FakeWindowsServicing? servicing = null,
        FakeProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            servicing: servicing ?? FakeWindowsServicing.Settled,
            handlers: handlers ?? FakeDiskCleanupHandlers.Windows());

    /// <summary>
    /// A directory at the top of the drive holding one file, with every entry in it dated
    /// <paramref name="age"/> ago, as an upgrade leaves it.
    /// </summary>
    private string Leftover(string relative, TimeSpan age, string file = "file.bin", int bytes = 4096)
    {
        var directory = Path.Combine(Volume, relative);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, file), new byte[bytes]);
        Aged(directory, age);
        return directory;
    }

    /// <summary>
    /// Every entry under <paramref name="directory"/> and the directory itself dated
    /// <paramref name="age"/> ago, deepest first so dating an entry cannot move its parent again.
    /// </summary>
    private static void Aged(string directory, TimeSpan age)
    {
        var when = DateTime.UtcNow - age;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, when);
            File.SetLastWriteTimeUtc(file, when);
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            Directory.SetCreationTimeUtc(child, when);
            Directory.SetLastWriteTimeUtc(child, when);
        }

        Directory.SetCreationTimeUtc(directory, when);
        Directory.SetLastWriteTimeUtc(directory, when);
    }

    /// <summary>The installation and its users, which §5.6 has to find standing afterwards.</summary>
    private string Users()
    {
        var users = Path.Combine(Volume, "Users");
        Directory.CreateDirectory(Path.Combine(users, "testuser"));
        File.WriteAllBytes(Path.Combine(users, "testuser", "notes.txt"), new byte[16]);
        return users;
    }

    [Fact]
    public async Task ReportsNotPresentOnADriveHoldingNoneOfThem()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task IsPresentWhereAPreviousInstallationIsThere()
    {
        Leftover("Windows.old", PastTheWindow);

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The declaration itself, pinned by name. A sixth name added without a test is a directory at the
    /// top of somebody's system drive that nobody decided to remove, and the drive itself and
    /// everything Windows keeps there are asserted to survive.
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheFiveDirectoriesAtTheTopOfTheSystemDriveAndNothingElse()
    {
        var root = Assert.Single(CreateProvider().Roots);

        Assert.Equal(Volume, root.Path);
        Assert.Equal(
            ["Windows.old", "$Windows.~BT", "$Windows.~WS", Path.Combine("ESD", "Windows"), Path.Combine("ESD", "Download")],
            root.Locations.Select(l => l.RelativePath));
        Assert.All(root.Locations, l => Assert.Equal(DeclaredLocationKind.Directory, l.Kind));
        Assert.Equal(SystemDriveRoot.Survivors, root.ProtectedNames);
        Assert.True(root.RequiresElevation);
    }

    /// <summary>
    /// The handler, never the path: every row is a <see cref="DiskCleanupStep"/> naming the handler
    /// Windows registers for it, and nothing is a deletion Deguffer performs itself.
    /// </summary>
    [Fact]
    public async Task OffersEachLeftoverThroughTheHandlerWindowsRegistersForIt()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var setup = Leftover("$Windows.~BT", PastTheWindow);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.IsType<DiskCleanupStep>(step));
        Assert.Equal(
            ["Previous Installations", "Temporary Setup Files"],
            plan.Steps.Cast<DiskCleanupStep>().Select(s => s.Handler));
        Assert.Equal([old, setup], plan.TargetedPaths);
        Assert.True(plan.RequiresElevation);
        Assert.All(plan.Steps, step => Assert.True(step.EstimatedBytes > 0));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    /// <summary>
    /// §6.3 at this boundary is the display form: the handler takes <c>C:\</c>, never
    /// <c>\\?\C:\</c>. Only what crossed can show which form it was, so the fake records it. After
    /// the run the previous installation is gone and everything Windows keeps beside it is standing.
    /// </summary>
    [Fact]
    public async Task HandsTheHandlerTheDriveInDisplayFormAndEverythingBesideItSurvives()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var users = Users();
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        var (handler, volume) = Assert.Single(handlers.Calls);
        Assert.Equal("Previous Installations", handler);
        Assert.Equal(LongPath.Display(Volume), volume);
        Assert.DoesNotContain(@"\\?\", volume, StringComparison.Ordinal);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(users));
        Assert.True(Directory.Exists(_system.WindowsDirectory));
        Assert.True(result.Succeeded);
        Assert.Equal(plan.EstimatedBytes, result.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Contains(result.Verification.Checks, c =>
            c.Subject.Equals(users, StringComparison.OrdinalIgnoreCase) && c.Outcome == VerificationOutcome.Survived);
    }

    /// <summary>
    /// §5.6: a handler that also took something Windows keeps at the top of the drive fails the run,
    /// however well it cleared what it was asked to.
    /// </summary>
    [Fact]
    public async Task AHandlerThatReachesPastWhatItWasAskedToClearFailsVerification()
    {
        Leftover("Windows.old", PastTheWindow);
        var users = Users();
        var provider = CreateProvider(FakeDiskCleanupHandlers.AlsoRemoving(users));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Contains(result.Verification.Failures, c => c.Subject.Equals(users, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2 at a volume root: the top of the drive is never listed, so a folder somebody keeps there is
    /// never a candidate, and it is standing after the run.
    /// </summary>
    [Fact]
    public async Task NeverReachesAFolderSomebodyKeepsAtTheTopOfTheDrive()
    {
        Leftover("Windows.old", PastTheWindow);
        var theirs = Leftover("Windows.old.backup", PastTheWindow);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.DoesNotContain(plan.TargetedPaths, p => p.Equals(theirs, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(theirs, "file.bin")));
    }

    /// <summary>
    /// Inside the uninstall window the upgrade can still be undone, and Windows removes all of this by
    /// itself when the window closes. The row says when it will be offered, and does not read
    /// "Already clear" over a folder that is full.
    /// </summary>
    [Fact]
    public async Task HoldsThePreviousInstallationBackWhileTheUpgradeCanStillBeUndone()
    {
        var old = Leftover("Windows.old", TimeSpan.FromDays(3));
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(old, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
        Assert.Contains(plan.Notes, n => n.Message.Contains("for another 7 days", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(Directory.Exists(old));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>The window is the machine's own, not Windows' default.</summary>
    [Fact]
    public async Task ReadsTheUninstallWindowFromTheMachine()
    {
        Leftover("Windows.old", PastTheWindow);

        var plan = await CreateProvider(servicing: new FakeWindowsServicing { UninstallWindowDays = 60 }).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
    }

    /// <summary>
    /// A restart still owed means an update has not finished, and what it wrote may be part of it.
    /// Nothing is offered, and the row says why in words of its own.
    /// </summary>
    [Fact]
    public async Task HoldsEverythingBackWhileARestartIsOwed()
    {
        var old = Leftover("Windows.old", PastTheWindow);

        var plan = await CreateProvider(servicing: new FakeWindowsServicing { IsRestartPending = true }).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WaitsForAnUpdate);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(old, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
        Assert.Contains(plan.Notes, n => n.Message.Contains("waiting for a restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoldsEverythingBackWhileSetupIsRunning()
    {
        Leftover("$Windows.~BT", PastTheWindow);

        var plan = await CreateProvider(inspector: new FakeProcessInspector("SetupHost")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WaitsForAnUpdate);
        Assert.Contains(plan.Notes, n => n.Message.Contains("SetupHost", StringComparison.Ordinal));
    }

    /// <summary>
    /// A restart that will move a file inside one folder holds that folder back, and only that one.
    /// </summary>
    [Fact]
    public async Task HoldsBackOnlyTheFolderARestartWillChange()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var setup = Leftover("$Windows.~BT", PastTheWindow);
        var servicing = new FakeWindowsServicing { PendingFileOperations = [Path.Combine(setup, "file.bin")] };

        var plan = await CreateProvider(servicing: servicing).PlanAsync();

        Assert.Equal([old], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(setup, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.UpdateInProgress);
    }

    /// <summary>
    /// One handler clears the downloaded installation files wherever its registration names them, so
    /// one row carries all of them: its figure is what Windows will take, every one is a target, and
    /// the <c>ESD</c> folder holding two of them is asserted to survive.
    /// </summary>
    [Fact]
    public async Task OneRowCarriesEveryDirectoryTheInstallationFilesHandlerClears()
    {
        var staging = Leftover("$Windows.~WS", PastTheWindow);
        var windows = Leftover(Path.Combine("ESD", "Windows"), PastTheWindow, bytes: 8192);
        var download = Leftover(Path.Combine("ESD", "Download"), PastTheWindow, bytes: 2048);
        var esd = Path.Combine(Volume, "ESD");
        Aged(esd, PastTheWindow);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        var step = Assert.IsType<DiskCleanupStep>(Assert.Single(plan.Steps));
        Assert.Equal(staging, step.Path);
        Assert.Equal([windows, download], step.AlsoClears);
        Assert.True(step.EstimatedBytes >= 4096 + 8192 + 2048);
        Assert.Equal([staging, windows, download], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(windows));
        Assert.False(Directory.Exists(download));
        Assert.True(Directory.Exists(esd));
        Assert.Equal(plan.EstimatedBytes, result.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Every directory a handler clears has to be past the window, because it clears them together.
    /// </summary>
    [Fact]
    public async Task AHandlerIsHeldBackWhileAnyOfItsDirectoriesIsInsideTheWindow()
    {
        Leftover("$Windows.~WS", PastTheWindow);
        Leftover(Path.Combine("ESD", "Download"), TimeSpan.FromDays(2));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
    }

    /// <summary>
    /// Where Windows does not register the handler, the folder is left standing rather than deleted by
    /// hand, because the handler is chosen for what it does besides deleting. The row does not read
    /// "Already clear" over it.
    /// </summary>
    [Fact]
    public async Task NeverDeletesByHandWhereTheHandlerIsMissing()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.NoneRegistered();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(old, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(Path.Combine(old, "file.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.1 is about whose knowledge decides. Where Windows' own cleanup finds nothing of its own in a
    /// folder — observed over a <c>$Windows.~WS</c> holding only setup sources — the folder is not
    /// offered, because the run would ask Windows to clear it and Windows would decline. The row does
    /// not read "Already clear" over what is still there.
    /// </summary>
    [Fact]
    public async Task LeavesAFolderWindowsOwnCleanupDoesNotCountAsItsOwn()
    {
        var staging = Leftover("$Windows.~WS", PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.FindingNothing();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Equal(["Windows ESD installation files"], handlers.Surveyed);
        Assert.Contains(plan.Notes, n => n.Message.Contains("finds nothing of its own", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(Path.Combine(staging, "file.bin")));
    }

    /// <summary>
    /// Unelevated, Windows' setup handlers answer "nothing to delete" whatever is on the disk, so the
    /// answer cannot be had. The row is offered as needing administrator rights, which it does in any
    /// case, and the plan made after elevating asks again.
    /// </summary>
    [Fact]
    public async Task OffersAFolderWhoseHandlerCannotBeAskedUntilElevated()
    {
        var old = Leftover("Windows.old", PastTheWindow);

        var plan = await CreateProvider(FakeDiskCleanupHandlers.Unasked()).PlanAsync();

        Assert.Equal([old], plan.TargetedPaths);
        Assert.True(plan.RequiresElevation);
    }

    /// <summary>
    /// The handler is asked last, because asking it can take as long as measuring an entire Windows
    /// installation, and a folder another rule already holds back is never asked about.
    /// </summary>
    [Fact]
    public async Task NeverAsksTheHandlerAboutAFolderAnotherRuleHoldsBack()
    {
        Leftover("Windows.old", TimeSpan.FromDays(3));
        var handlers = FakeDiskCleanupHandlers.Windows();

        await CreateProvider(handlers).PlanAsync();

        Assert.Empty(handlers.Surveyed);
    }

    /// <summary>
    /// A handler that finds nothing of its own at the moment it runs says so, and the run reports what
    /// the disk still holds rather than a cleanup that did not happen.
    /// </summary>
    [Fact]
    public async Task AHandlerThatFindsNothingWhenItRunsSaysSo()
    {
        Leftover("Windows.old", PastTheWindow);
        var provider = CreateProvider(new FakeDiskCleanupHandlers(
            (_, _) => new DiskCleanupOutcome(Ran: true, "Windows found nothing of this to clear.")));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.False(step.Succeeded);
        Assert.StartsWith("Windows found nothing of this to clear.", step.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §9: an Outlook data file left in the previous installation's profile is exactly the kind of
    /// thing somebody goes back into <c>Windows.old</c> for. Windows clears the folder whole, so the
    /// row is withheld and the file proved standing.
    /// </summary>
    [Fact]
    public async Task AnOutlookDataFileInThePreviousInstallationWithholdsIt()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var store = Path.Combine(old, "Users", "testuser", "Documents", "Outlook Files", "archive.pst");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllBytes(store, new byte[1024]);
        Aged(old, PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HoldsMailStores);

        await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(store));
    }

    /// <summary>
    /// §9 again, at the moment Windows is asked: a store saved into the previous installation while the
    /// preview sat on screen is found on the disk, and the handler is never run over it.
    /// </summary>
    [Fact]
    public async Task AnOutlookDataFileThatArrivesAfterThePreviewStopsTheHandler()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();
        Assert.Single(plan.Steps);

        var store = Path.Combine(old, "mailbox.ost");
        File.WriteAllBytes(store, new byte[1024]);

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(store));
        var step = Assert.Single(result.Steps);
        Assert.False(step.Succeeded);
        Assert.Equal(1, step.MailStores);
    }

    /// <summary>
    /// The guard on recently changed files cannot be honoured by a handler that clears whole, so a
    /// folder holding anything it would keep is withdrawn rather than cleared with it.
    /// </summary>
    [Fact]
    public async Task TheGuardWithdrawsAFolderHoldingAnythingItWouldKeep()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var recent = Path.Combine(old, "Users", "testuser", "recent.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(recent)!);
        File.WriteAllBytes(recent, new byte[64]);

        // Every folder dated back and the file left new, so the window is past and only the guard's
        // walk of the whole tree can find it.
        foreach (var folder in new[] { Path.GetDirectoryName(recent)!, Path.Combine(old, "Users"), old })
        {
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow - PastTheWindow);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - PastTheWindow);
        }
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow));

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Windows clears it whole", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// The disk is the evidence and the handler's answer the explanation: a refusal is reported with
    /// its words, and the step fails over a folder that is exactly as full as it was.
    /// </summary>
    [Fact]
    public async Task AHandlerThatRefusesFailsTheStepAndSaysWhy()
    {
        var old = Leftover("Windows.old", PastTheWindow);
        var provider = CreateProvider(FakeDiskCleanupHandlers.Refusing("Windows' cleanup stopped (0x80070005)."));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.False(step.Succeeded);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Contains("0x80070005", step.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(old));
    }

    /// <summary>A handler that reports success over a folder still full is not believed.</summary>
    [Fact]
    public async Task AHandlerThatReportsSuccessAndClearsNothingFailsTheStep()
    {
        Leftover("Windows.old", PastTheWindow);
        var provider = CreateProvider(FakeDiskCleanupHandlers.DoingNothing());

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.False(step.Succeeded);
        Assert.Contains("still there", step.Message, StringComparison.Ordinal);
    }
}
