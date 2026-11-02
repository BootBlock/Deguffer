using Deguffer.Core.Execution;
using Deguffer.Core.Profiles;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Removing an offered profile: through Windows' own removal and nothing else, judged again immediately
/// before, and proved afterwards (§5.6) by every other profile Windows still lists.
/// </summary>
public sealed class ProfileRemovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeUserProfiles _profiles;
    private readonly UserProfile _running;
    private readonly UserProfile _colleague;

    public ProfileRemovalTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _profiles = new FakeUserProfiles(Path.Combine(_temp.Path, "Users"));
        Directory.CreateDirectory(_environment.UserProfile);
        _running = _profiles.AddEntry(new UserProfile(_environment.UserSecurityIdentifier!, _environment.UserProfile, ProfileLoad.Loaded, null));
        _colleague = _profiles.AddLocal(1003, "colleague");
        _profiles.Accounts[_running.Sid] = new AccountReading(AccountAnswer.Exists);
        _profiles.Accounts[_colleague.Sid] = new AccountReading(AccountAnswer.Exists);
    }

    public void Dispose() => _temp.Dispose();

    private OtherAccountProfilesProvider Provider() =>
        new(_profiles, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    [Fact]
    public async Task TheRemovalReachesWindowsOwnRemovalAndProvesEveryOtherProfileStood()
    {
        var gone = _profiles.AddLocal(1002, "former", bytes: 4000);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal([gone.Sid], _profiles.Removed);
        Assert.True(Assert.Single(result.Steps).Succeeded);
        Assert.Equal(4000, result.BytesReclaimed);
        Assert.False(Directory.Exists(gone.Folder));
        Assert.True(Directory.Exists(_colleague.Folder));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        var checks = result.Verification.Checks;
        Assert.Contains(checks, check => check.Subject == ProfileReport.Name(_running)
            && check.Outcome is VerificationOutcome.Survived
            && check.Reason.Contains("running as", StringComparison.Ordinal));
        Assert.Contains(checks, check => check.Subject == ProfileReport.Name(_colleague) && check.Outcome is VerificationOutcome.Survived);
        Assert.Contains(checks, check => check.Subject == _profiles.ProfilesFolder && check.Outcome is VerificationOutcome.Survived);
        Assert.DoesNotContain(checks, check => check.Subject == ProfileReport.Name(gone));
    }

    /// <summary>§5.6: a sibling profile that went with the removal fails the run, and is named.</summary>
    [Fact]
    public async Task TheCheckFailsWhenASiblingProfileWentToo()
    {
        _profiles.AddLocal(1002, "former");
        _profiles.OnRemoved = _ => _profiles.Profiles.Remove(_colleague);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var failure = Assert.Single(result.Verification!.Failures);
        Assert.Equal(ProfileReport.Name(_colleague), failure.Subject);
        Assert.StartsWith("MISSING", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckFailsWhenASiblingProfilesFolderChanged()
    {
        _profiles.AddLocal(1002, "former");
        _profiles.OnRemoved = _ =>
        {
            _profiles.Profiles.Remove(_colleague);
            _profiles.Profiles.Add(_colleague with { Folder = Path.Combine(_profiles.ProfilesFolder, "moved") });
        };

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var failure = Assert.Single(result.Verification!.Failures);
        Assert.Equal(ProfileReport.Name(_colleague), failure.Subject);
        Assert.StartsWith("CHANGED", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckFailsWhenASiblingsFolderWasDeleted()
    {
        _profiles.AddLocal(1002, "former");
        _profiles.OnRemoved = _ => Directory.Delete(_colleague.Folder!, recursive: true);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var failure = Assert.Single(result.Verification!.Failures);
        Assert.Equal(ProfileReport.Name(_colleague), failure.Subject);
        Assert.Contains("its folder is gone", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>A profile the user did not choose is proved standing with the rest.</summary>
    [Fact]
    public async Task ADeclinedProfileIsProvedStanding()
    {
        var chosen = _profiles.AddLocal(1002, "former");
        var declined = _profiles.AddLocal(1004, "tester");

        var provider = Provider();
        var plan = await provider.PlanAsync();
        var narrowed = plan.NarrowedTo([.. plan.Steps.OfType<RemoveProfileStep>().Where(step => step.Profile.Sid == chosen.Sid)]);
        var result = await provider.ExecuteAsync(narrowed);

        Assert.Equal([chosen.Sid], _profiles.Removed);
        Assert.True(Directory.Exists(declined.Folder));
        Assert.Contains(result.Verification!.Checks, check => check.Subject == ProfileReport.Name(declined) && check.Outcome is VerificationOutcome.Survived);
    }

    /// <summary>An account made again between the preview and the clean owns the profile again.</summary>
    [Fact]
    public async Task AProfileWhoseAccountReturnedIsNotRemoved()
    {
        var gone = _profiles.AddLocal(1002, "former");
        var provider = Provider();
        var plan = await provider.PlanAsync();

        _profiles.Accounts[gone.Sid] = new AccountReading(AccountAnswer.Exists);
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_profiles.Removed);
        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("its account still exists", outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(gone.Folder));
    }

    [Fact]
    public async Task AProfileLoadedSinceThePreviewIsNotRemoved()
    {
        var gone = _profiles.AddLocal(1002, "former");
        var provider = Provider();
        var plan = await provider.PlanAsync();

        _profiles.Profiles[_profiles.Profiles.IndexOf(gone)] = gone with { Load = ProfileLoad.Loaded };
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_profiles.Removed);
        Assert.Contains("Windows has it loaded", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWindowsNowRecordsDifferentlyIsNotRemoved()
    {
        var gone = _profiles.AddLocal(1002, "former");
        var provider = Provider();
        var plan = await provider.PlanAsync();

        var moved = Path.Combine(_profiles.ProfilesFolder, "former2");
        Directory.CreateDirectory(moved);
        _profiles.Profiles[_profiles.Profiles.IndexOf(gone)] = gone with { Folder = moved };
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_profiles.Removed);
        Assert.Contains("different folder", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    /// <summary>§9: an Outlook data file that arrives after the preview withholds Windows' whole removal.</summary>
    [Fact]
    public async Task AnOutlookDataFileThatArrivedAfterThePreviewStopsTheRemoval()
    {
        var gone = _profiles.AddLocal(1002, "former");
        var provider = Provider();
        var plan = await provider.PlanAsync();

        File.WriteAllBytes(Path.Combine(gone.Folder!, "mail.ost"), new byte[10]);
        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_profiles.Removed);
        Assert.StartsWith("Not removed", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovalWindowsRefusedIsReportedAndCountsNothing()
    {
        var gone = _profiles.AddLocal(1002, "former");
        _profiles.Removals[gone.Sid] = new ProfileRemoval(ProfileRemovalAnswer.NeedsElevation, 5);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains("only for an administrator", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("folder and its entry are both still there", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>The executor never falls back to the machine's profiles: a plan holding the step needs the route given.</summary>
    [Fact]
    public async Task AnExecutorGivenNoRouteRefusesThePlanBeforeAnythingRuns()
    {
        _profiles.AddLocal(1002, "former");
        var plan = await Provider().PlanAsync();
        var executor = new PlanExecutor(new FakeProcessRunner(), Scanning.ParallelEnumerationScanner.Default, RefusalRecord.For(_environment));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan, null, null, null, CancellationToken.None));
        Assert.Empty(_profiles.Removed);
    }
}
