using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the temporary files row and the test browser profiles row decided at Preview about the
/// entries of a temporary folder a program is using, asked again at Clean. A preview can sit on
/// screen indefinitely, and an installer unpacking into an old entry, or a test run starting a browser
/// on an old profile, makes the preview's answer wrong in the direction that deletes.
///
/// <para>Each test plans with nothing running, starts a program the way a user would between the two
/// presses, and then cleans. Everything runs against an invented profile and Windows directory
/// through <see cref="FakeUserEnvironment"/>, <see cref="FakeSystemDirectories"/> and
/// <see cref="FakeLiveTreeInspector"/>.</para>
/// </summary>
public sealed class ScratchFolderCleanRecheckTests : IDisposable
{
    private static readonly TimeSpan Old = TimeSpan.FromDays(30);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;

    public ScratchFolderCleanRecheckTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempDirectoryProvider TemporaryFilesRow(params ITemporaryFolderTenant[] tenants) =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: _liveTrees,
            preferences: new FakePreferences(AppPreferences.Default),
            tenants: tenants);

    private TestBrowserProfileProvider TestProfilesRow() =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: _liveTrees,
            preferences: new FakePreferences(AppPreferences.Default));

    /// <summary>A file in this account's temporary folder old enough for either row to offer.</summary>
    private string Abandoned(int bytes, params string[] segments) =>
        TempDirectory.Age(_temp.CreateFile(bytes, ["temp", .. segments]), Old);

    /// <summary>A profile holding one old file, with its folders aged after their contents.</summary>
    private string AbandonedProfile(string name)
    {
        Abandoned(1024, name, "Default", "Preferences");
        TempDirectory.Age(Path.Combine(UserTemp, name, "Default"), Old);
        return TempDirectory.Age(Path.Combine(UserTemp, name), Old);
    }

    /// <summary>
    /// The case §5.3 was written about, arriving after the preview. An installer starts working in an
    /// entry nothing was using when the plan was made. The clean spares that entry whole, as the
    /// preview would have, and still empties the rest of the folder around it.
    /// </summary>
    [Fact]
    public async Task AnEntryAProgramStartedWorkingInAfterThePreviewIsSpared()
    {
        var taken = _temp.CreateDirectory("temp", "setup-unpack");
        var working = Abandoned(8192, "setup-unpack", "payload.cab");
        var abandonedFile = Abandoned(1024, "abandoned.tmp");
        var abandonedFolder = Abandoned(2048, "old-build", "obj.tmp");

        var provider = TemporaryFilesRow();
        var plan = await provider.PlanAsync();

        Assert.Empty(Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp).Spared);

        _liveTrees.WithProgram("setup", workingDirectory: taken);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(working), "an entry a program started working in after the preview was emptied");
        Assert.False(File.Exists(abandonedFile), "the rest of the folder was not emptied");
        Assert.False(File.Exists(abandonedFolder), "the rest of the folder was not emptied");
        AssertProvedStanding(result, taken);
    }

    /// <summary>
    /// An entry another row offers is that row's to ask about. This row leaves it alone already, so
    /// the clean neither spares it nor asserts it survived: a run with both rows ticked removes it
    /// legitimately, and §5.6 must not read that as a failure.
    /// </summary>
    [Fact]
    public async Task AnEntryAnotherRowOwnsIsNotSparedByThisRowsQuestion()
    {
        var owned = _temp.CreateDirectory("temp", "tool-cache");
        var compiled = Abandoned(8192, "tool-cache", "compiled.bin");
        var abandonedFile = Abandoned(1024, "abandoned.tmp");

        var provider = TemporaryFilesRow(new FakeTemporaryFolderTenant("Tool cache", "tool-cache"));
        var plan = await provider.PlanAsync();

        _liveTrees.WithProgram("tool", workingDirectory: owned);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(compiled), "the temporary files row took another row's entry");
        Assert.False(File.Exists(abandonedFile));
        Assert.Equal(0, result.SparedCount);
        Assert.DoesNotContain(result.Verification!.Checks, c => c.Subject.Equals(owned, StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A test run starts a browser on a profile nothing was using when the plan was made. The browser
    /// names its profile only on its command line, and the clean asks the same question the plan did,
    /// so that profile stays whole. A profile nothing started with still goes.
    /// </summary>
    [Fact]
    public async Task AProfileABrowserWasStartedWithAfterThePreviewIsNotRemoved()
    {
        var live = AbandonedProfile("playwright_chromiumdev_profile-a1B2c3");
        var abandoned = AbandonedProfile("puppeteer_dev_chrome_profile-g7H8i9");

        var provider = TestProfilesRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(live, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(abandoned, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _liveTrees.WithProgram("chrome-headless-shell", arguments: [live]);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(live, "Default", "Preferences")),
            "a profile a browser was started with after the preview was removed");
        Assert.False(Directory.Exists(abandoned), "a profile nothing was using was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: chrome-headless-shell was started with it.");
        AssertProvedStanding(result, live);
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }
}
