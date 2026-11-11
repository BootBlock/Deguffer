using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The files a Windows upgrade set aside rather than carry over, removed by Windows' own <em>Upgrade
/// Discarded Files</em> handler once the upgrade can no longer be undone.
///
/// <para>What holds a folder back is <see cref="SetupCleanupProviderBase"/>'s, and
/// <see cref="PreviousWindowsInstallationProviderTests"/> covers it rule by rule. These pin what is this
/// provider's own: the registration it clears through, the three directories it names, its tier, and
/// that the previous installation beside them survives it.</para>
/// </summary>
public sealed class UpgradeDiscardedFilesProviderTests : IDisposable
{
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromDays(20);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public UpgradeDiscardedFilesProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Volume => _system.SystemDrive;

    private UpgradeDiscardedFilesProvider CreateProvider(FakeDiskCleanupHandlers? handlers = null) =>
        new(
            handlers ?? FakeDiskCleanupHandlers.Windows(),
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            servicing: FakeWindowsServicing.Settled);

    /// <summary>A directory at the top of the drive holding one file, everything in it dated <paramref name="age"/> ago.</summary>
    private string Leftover(string relative, TimeSpan age, int bytes = 4096)
    {
        var directory = Path.Combine(Volume, relative);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "file.bin");
        File.WriteAllBytes(file, new byte[bytes]);

        var when = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(file, when);
        File.SetCreationTimeUtc(file, when);
        Directory.SetLastWriteTimeUtc(directory, when);
        Directory.SetCreationTimeUtc(directory, when);

        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentOnADriveHoldingNoneOfThem()
    {
        Leftover("Windows.old", PastTheWindow);
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task IsPresentWhereAnUpgradeSetFilesAside()
    {
        Leftover("$Windows.~LS", PastTheWindow);

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The declaration is the registration's <c>SetupDirectories</c> value, in its order, at the top of
    /// the system drive, with everything Windows keeps there named to survive.
    /// </summary>
    [Fact]
    public void TheDeclarationIsTheThreeDirectoriesTheHandlerNamesAndNothingElse()
    {
        var root = Assert.Single(CreateProvider().Roots);

        Assert.Equal(Volume, root.Path);
        Assert.Equal(["$WINDOWS.~Q", "$INPLACE.~TR", "$Windows.~LS"], root.Locations.Select(l => l.RelativePath));
        Assert.All(root.Locations, l => Assert.Equal(DeclaredLocationKind.Directory, l.Kind));
        Assert.Equal(SystemDriveRoot.Survivors, root.ProtectedNames);
        Assert.True(root.RequiresElevation);
    }

    /// <summary>
    /// Two providers, one handler class: neither may name a directory the other clears, or one row's
    /// tier would decide for the other's contents.
    /// </summary>
    [Fact]
    public void NamesNothingThePreviousInstallationRowClears()
    {
        var previous = new PreviousWindowsInstallationProvider(FakeDiskCleanupHandlers.Windows(), _environment, system: _system);
        var theirs = previous.Roots.SelectMany(r => r.Locations).Select(l => l.RelativePath);
        var ours = CreateProvider().Roots.SelectMany(r => r.Locations).Select(l => l.RelativePath);

        Assert.Empty(ours.Intersect(theirs, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One row, through the handler, carrying every directory it clears, at Tier 3. After the run the
    /// three are gone, and the previous installation beside them, the users' files and the top of the
    /// drive are standing: §5.6's negative.
    /// </summary>
    [Fact]
    public async Task OffersOneRowThroughItsHandlerAndEverythingBesideItSurvives()
    {
        var q = Leftover("$WINDOWS.~Q", PastTheWindow);
        var inplace = Leftover("$INPLACE.~TR", PastTheWindow, bytes: 8192);
        var ls = Leftover("$Windows.~LS", PastTheWindow, bytes: 2048);
        var old = Leftover("Windows.old", PastTheWindow);
        var users = Leftover("Users", PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.UserData, plan.Tier);
        var step = Assert.IsType<DiskCleanupStep>(Assert.Single(plan.Steps));
        Assert.Equal("Upgrade Discarded Files", step.Handler);
        Assert.Equal(q, step.Path);
        Assert.Equal([inplace, ls], step.AlsoClears);
        Assert.True(step.EstimatedBytes >= 4096 + 8192 + 2048);
        Assert.True(plan.RequiresElevation);
        Assert.Equal([("Upgrade Discarded Files", DiskCleanupQuestion.Starts)], handlers.Surveyed);

        var result = await provider.ExecuteAsync(plan);

        var (handler, volume) = Assert.Single(handlers.Calls);
        Assert.Equal("Upgrade Discarded Files", handler);
        Assert.Equal(LongPath.Display(Volume), volume);
        Assert.DoesNotContain(@"\\?\", volume, StringComparison.Ordinal);

        Assert.False(Directory.Exists(q));
        Assert.False(Directory.Exists(inplace));
        Assert.False(Directory.Exists(ls));
        Assert.True(File.Exists(Path.Combine(old, "file.bin")));
        Assert.True(File.Exists(Path.Combine(users, "file.bin")));
        Assert.True(Directory.Exists(Volume));
        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Contains(result.Verification.Checks, c =>
            c.Subject.Equals(users, StringComparison.OrdinalIgnoreCase) && c.Outcome == VerificationOutcome.Survived);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        Leftover("$WINDOWS.~Q", PastTheWindow);
        Leftover("$INPLACE.~TR", PastTheWindow);
        Leftover("$Windows.~LS", PastTheWindow);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// Inside the uninstall window nothing is offered, and the row says it is the time to check that
    /// nothing went missing rather than that Windows will remove the folder itself, which no source
    /// says of these.
    /// </summary>
    [Fact]
    public async Task HoldsTheFilesBackWhileTheUpgradeCanStillBeUndone()
    {
        var ls = Leftover("$Windows.~LS", TimeSpan.FromDays(3));
        var handlers = FakeDiskCleanupHandlers.Windows();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(ls, StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
        var note = Assert.Single(plan.Notes, n => n.Message.Contains("for another 7 days", StringComparison.Ordinal));
        Assert.Contains("nothing of yours went missing", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("removes it by itself", note.Message, StringComparison.Ordinal);

        await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(Path.Combine(ls, "file.bin")));
    }

    /// <summary>
    /// Where Windows does not register the handler, the folders are left standing rather than deleted
    /// by hand, and the row does not read "Already clear" over them.
    /// </summary>
    [Fact]
    public async Task NeverDeletesByHandWhereTheHandlerIsMissing()
    {
        var q = Leftover("$WINDOWS.~Q", PastTheWindow);
        var handlers = FakeDiskCleanupHandlers.NoneRegistered();
        var provider = CreateProvider(handlers);

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(handlers.Calls);
        Assert.True(File.Exists(Path.Combine(q, "file.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
