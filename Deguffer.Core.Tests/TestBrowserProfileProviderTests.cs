using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The profiles test runners leave in a temporary folder: which children are recognised, which
/// survive (§5.6), and the two halves of §5.3 — the age limit and the running browser.
///
/// <para>Everything runs against a synthetic profile and a synthetic Windows directory, with the
/// profile names in the exact shapes Playwright and Puppeteer write.</para>
/// </summary>
public sealed class TestBrowserProfileProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TestBrowserProfileProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private string MachineTemp => Path.Combine(_system.WindowsDirectory, "Temp");

    private TestBrowserProfileProvider CreateProvider(
        ILiveTreeInspector? liveTrees = null,
        AppPreferences? preferences = null,
        IProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(preferences ?? AppPreferences.Default));

    /// <summary>A profile in this account's temporary folder holding one old file, returned as its path.</summary>
    private string AbandonedProfile(string name, int bytes = 1024)
    {
        TempDirectory.Age(_temp.CreateFile(bytes, "temp", name, "Default", "Preferences"), TimeSpan.FromDays(30));
        return Path.Combine(UserTemp, name);
    }

    /// <summary>
    /// §5.2 in a folder that belongs to nobody: every name either tool writes is offered, and every
    /// sibling survives the clean — including the near misses a looser rule would take.
    /// </summary>
    [Fact]
    public async Task OffersEachProfileEitherToolWritesAndNothingElseInTheFolder()
    {
        string[] profiles =
        [
            AbandonedProfile("playwright_chromiumdev_profile-a1B2c3"),
            AbandonedProfile("playwright_firefoxdev_profile-d4E5f6"),
            AbandonedProfile("puppeteer_dev_chrome_profile-g7H8i9"),
            AbandonedProfile("puppeteer_dev_firefox_profile-j1K2l3"),
        ];

        string[] siblings =
        [
            _temp.CreateFile(64, "temp", "playwright-artifacts-m4N5o6", "trace.zip"),
            _temp.CreateFile(64, "temp", "playwright_chromiumdev_profile-short", "keep"),
            _temp.CreateFile(64, "temp", "playwright_chromiumdev_profile-toolong1", "keep"),
            _temp.CreateFile(64, "temp", "Playwright_chromiumdev_profile-p7Q8r9", "keep"),
            _temp.CreateFile(64, "temp", "playwright_edgedev_profile-s1T2u3", "keep"),
            _temp.CreateFile(64, "temp", "unrelated", "playwright_chromiumdev_profile-v4W5x6", "keep"),
            _temp.CreateFile(64, "temp", "playwright_firefoxdev_profile-y7Z8a9"),
        ];

        foreach (var sibling in siblings)
        {
            TempDirectory.Age(sibling, TimeSpan.FromDays(30));
        }

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(profiles.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.TargetedPaths, path => Assert.DoesNotContain(@"\\?\", path, StringComparison.Ordinal));
        Assert.Equal(
            ["Playwright Chromium", "Playwright Firefox", "Puppeteer Chrome", "Puppeteer Firefox"],
            plan.Steps.OfType<DeleteStep>().Select(s => s.Group).Order(StringComparer.Ordinal));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(profiles, profile => Assert.False(Directory.Exists(profile), $"{profile} survived"));
        Assert.All(siblings, sibling => Assert.True(File.Exists(sibling), $"{sibling} was deleted"));
        Assert.True(Directory.Exists(UserTemp), "the temporary folder itself was deleted");
    }

    /// <summary>
    /// Playwright's WebKit is never given its profile, so the folder it leaves is empty. It frees no
    /// bytes and is still the whole of what this row removes, so it is offered as a leftover rather
    /// than hidden as a step with nothing in it.
    /// </summary>
    [Fact]
    public async Task OffersAnEmptyWebKitProfileAsALeftover()
    {
        var profile = _temp.CreateDirectory("temp", "playwright_webkitdev_profile-b1C2d3");

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteStep>());
        Assert.Equal(profile, step.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(step.IsLeftover);
        Assert.Equal("Playwright WebKit", step.Group);
    }

    /// <summary>
    /// §5.3's age limit, which the temporary files row applies too: a profile written this minute
    /// belongs to a test that may be running now. At zero the limit is off, and it is offered.
    /// </summary>
    [Fact]
    public async Task HoldsBackARecentProfileUnderTheTemporaryFilesAgeLimit()
    {
        var recent = _temp.CreateFile(4096, "temp", "playwright_chromiumdev_profile-e4F5g6", "Default", "Cookies");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.Keep.IsOn);
        Assert.Equal(0, plan.EstimatedBytes);

        await CreateProvider().ExecuteAsync(plan);
        Assert.True(File.Exists(recent), "a profile written this minute was deleted");

        var unlimited = await CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 }).PlanAsync();

        Assert.Equal(4096, unlimited.EstimatedBytes);
    }

    /// <summary>
    /// A browser a test started names its profile only on its command line. That profile is never a
    /// target however old its files are, the plan says what is using it, and §5.6 asserts it
    /// survived.
    /// </summary>
    [Fact]
    public async Task LeavesAProfileARunningBrowserWasStartedWith()
    {
        var live = AbandonedProfile("playwright_chromiumdev_profile-h7I8j9");
        var abandoned = AbandonedProfile("playwright_chromiumdev_profile-k1L2m3");

        var liveTrees = FakeLiveTreeInspector.NothingLive
            .WithProgram("chrome-headless-shell", arguments: [live]);

        var provider = CreateProvider(liveTrees);
        var plan = await provider.PlanAsync();

        Assert.Equal([abandoned], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(live, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("chrome-headless-shell was started with it", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(live), "a profile a running browser was started with was deleted");
        Assert.False(Directory.Exists(abandoned));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A program working inside a profile is using it too, whatever it was started with.</summary>
    [Fact]
    public async Task LeavesAProfileAProgramIsWorkingIn()
    {
        var live = AbandonedProfile("puppeteer_dev_chrome_profile-n4O5p6");

        var liveTrees = FakeLiveTreeInspector.NothingLive
            .WithProgram("node", workingDirectory: Path.Combine(live, "Default"));

        var plan = await CreateProvider(liveTrees).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(live, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A check that could not run must not look like one that found nothing, so the row says so.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenItCouldNotTellWhatIsRunning()
    {
        AbandonedProfile("playwright_chromiumdev_profile-q7R8s9");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not check whether anything is using these", StringComparison.Ordinal));
    }

    /// <summary>
    /// Present only where a profile is, and without asking whether either tool is installed: the
    /// machine with abandoned profiles is often one where the tool has since been removed.
    /// </summary>
    [Fact]
    public async Task IsPresentOnlyWhereATemporaryFolderHoldsAProfile()
    {
        _temp.CreateFile(64, "temp", "someone-elses", "file.tmp");

        Assert.False(await CreateProvider().IsPresentAsync());

        _temp.CreateDirectory("temp", "puppeteer_dev_firefox_profile-t1U2v3");

        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A test run as a service writes its profiles to the machine's temporary folder, and removing
    /// one from there needs administrator rights.
    /// </summary>
    [Fact]
    public async Task FindsProfilesInTheMachinesTemporaryFolderAndSaysTheyNeedElevation()
    {
        TempDirectory.Age(
            _temp.CreateFile(512, "Windows", "Temp", "playwright_chromiumdev_profile-w4X5y6", "Local State"),
            TimeSpan.FromDays(30));

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteStep>());
        Assert.Equal(Path.Combine(MachineTemp, "playwright_chromiumdev_profile-w4X5y6"), step.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(step.RequiresElevation);
    }

    /// <summary>
    /// A link named like a profile is not one: what it points at was never classified, so it is
    /// named and never followed.
    /// </summary>
    [Fact]
    public async Task LeavesALinkNamedLikeAProfileAlone()
    {
        var outside = TempDirectory.Age(_temp.CreateFile(256, "elsewhere", "precious.txt"), TimeSpan.FromDays(30));
        Directory.CreateSymbolicLink(
            Path.Combine(_temp.CreateDirectory("temp"), "playwright_chromiumdev_profile-z7A8b9"),
            Path.GetDirectoryName(outside)!);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("playwright_chromiumdev_profile-z7A8b9", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);
        Assert.True(File.Exists(outside));
    }

    /// <summary>
    /// Explore refuses a profile a running browser is using, as the plan does (§7.1).
    /// </summary>
    [Fact]
    public async Task DeclaresAProfileInUseToExploreAsARootRecognisingNothing()
    {
        var live = AbandonedProfile("playwright_firefoxdev_profile-c1D2e3");
        AbandonedProfile("playwright_firefoxdev_profile-f4G5h6");

        var liveTrees = FakeLiveTreeInspector.NothingLive.WithProgram("firefox", arguments: [live]);

        var roots = await CreateProvider(liveTrees).DiscoverToolRootsAsync();

        var root = Assert.Single(roots);
        Assert.Equal(live, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.False(root.Recognises("Default"));
    }
}
