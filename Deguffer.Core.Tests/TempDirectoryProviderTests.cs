using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3 is this provider, so it is what these assert: the cut-off nothing may loosen, the entries a
/// running program is using, and the folder itself surviving a clean.
///
/// <para>Everything runs against a synthetic profile and a synthetic Windows directory. A test that
/// emptied the developer's real <c>%TEMP%</c> to prove a rule would be an unusually expensive way to
/// assert one.</para>
/// </summary>
public sealed class TempDirectoryProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TempDirectoryProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>This account's scratch folder, which <see cref="FakeUserEnvironment"/> creates.</summary>
    private string UserTemp => _environment.TempPath;

    private string MachineTemp => Path.Combine(_system.WindowsDirectory, "Temp");

    private TempDirectoryProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    /// <summary>A file old enough for the provider to offer, returned so a test can name it.</summary>
    private string Abandoned(int bytes, params string[] segments) =>
        TempDirectory.Age(_temp.CreateFile(bytes, segments), TimeSpan.FromDays(30));

    [Fact]
    public async Task PlansOneStepPerTemporaryFolderThatIsThere()
    {
        Abandoned(1024, "temp", "old.tmp");
        Abandoned(2048, "Windows", "Temp", "older.tmp");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            [UserTemp, MachineTemp],
            plan.Steps.OfType<ClearDirectoryStep>().Select(s => s.Path));

        Assert.Equal(1024 + 2048, plan.EstimatedBytes);
    }

    /// <summary>
    /// §5.2 against a folder every program on the machine expects to find. Windows does not put a
    /// deleted temporary folder back, so this is the assertion the whole step kind exists for — and
    /// it is made about a run that happened, not about the plan's wording.
    /// </summary>
    [Fact]
    public async Task EmptiesTheFolderAndLeavesTheFolderItself()
    {
        Abandoned(1024, "temp", "old.tmp");
        Abandoned(2048, "temp", "nested", "older.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(UserTemp, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(UserTemp), "the temporary folder itself was deleted");
        Assert.Empty(Directory.EnumerateFileSystemEntries(UserTemp));
        Assert.Equal(1024 + 2048, result.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The cut-off, on both sides of it. A file written a moment ago is what §5.3's audit found 344
    /// MB of, indistinguishable by name and location from the abandoned one beside it.
    /// </summary>
    [Fact]
    public async Task OffersWhatNothingHasTouchedForAWeekAndNothingNewer()
    {
        var stale = Abandoned(4096, "temp", "finished.tmp");
        var live = _temp.CreateFile(8192, "temp", "being-written.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(4096, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(live), "a file written this minute was deleted from the temporary folder");
        Assert.Equal(4096, result.BytesReclaimed);
    }

    /// <summary>
    /// The boundary itself, from the side that matters. Six days is inside the window on any
    /// machine, and a cut-off that had been silently switched off would take it.
    /// </summary>
    [Fact]
    public async Task LeavesSomethingSixDaysOldAlone()
    {
        TempDirectory.Age(_temp.CreateFile(4096, "temp", "recent.tmp"), TimeSpan.FromDays(6));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(0, plan.EstimatedBytes);
        Assert.True(plan.HasRecentContentHeldBack, "a folder full of recent files reported as clear");
    }

    /// <summary>
    /// The floor is Deguffer's, not the user's, so a shorter window they set cannot widen what the
    /// removal takes. This is the whole reason <see cref="MinimumAge.Stricter"/> exists — the plan
    /// carries the guard the removal applies, and stamping the user's over the top would have
    /// deleted the file below.
    /// </summary>
    [Fact]
    public async Task AShorterGuardTheUserSetDoesNotLoosenTheFloor()
    {
        var twoDaysOld = TempDirectory.Age(_temp.CreateFile(4096, "temp", "yesterday.tmp"), TimeSpan.FromDays(2));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.WithinHours(1, DateTime.UtcNow));

        Assert.Equal(0, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(twoDaysOld), "the user's shorter window widened what the clean took");
        Assert.Equal(0, result.BytesReclaimed);
    }

    /// <summary>
    /// A longer window the user set is stricter than the floor, and wins. The two guards compose in
    /// both directions or the combination is not a combination.
    /// </summary>
    [Fact]
    public async Task ALongerGuardTheUserSetIsHonouredOverTheFloor()
    {
        var tenDaysOld = TempDirectory.Age(_temp.CreateFile(4096, "temp", "old.tmp"), TimeSpan.FromDays(10));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(30), DateTime.UtcNow));

        Assert.Equal(0, plan.EstimatedBytes);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(tenDaysOld), "the floor overrode a stricter setting the user chose");
    }

    /// <summary>
    /// §5.3's second exclusion, end to end: the entry is spared, its bytes come out of the estimate,
    /// the run leaves it, and §5.6 asserts it survived.
    ///
    /// <para>The subtraction is the half that would not look wrong if it were missing. A step naming
    /// its spared entries while still counting them promises bytes the clean has already undertaken
    /// not to take.</para>
    /// </summary>
    [Fact]
    public async Task SparesAnEntryARunningProgramIsUsingAndTakesItsBytesOutOfTheEstimate()
    {
        var busy = _temp.CreateDirectory("temp", "live-session");
        Abandoned(8192, "temp", "live-session", "working.txt");
        Abandoned(1024, "temp", "abandoned.tmp");

        var provider = CreateProvider(new FakeLiveTreeInspector(busy));
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);

        Assert.Equal([busy], step.Spared);
        Assert.Equal(1024, step.EstimatedBytes);

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(busy, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore && p.HeldContentBefore);

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("live-session", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(busy, "working.txt")), "a live scratch directory was emptied");
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6 catching the failure this provider can actually have. An over-broad clear leaves every
    /// folder standing and empties one it promised not to, which no assertion about existence can
    /// see — so the spared entry is recorded as having held content, and emptying it is an alarm.
    /// </summary>
    [Fact]
    public async Task ReportsAFailureWhenSomethingEmptiesTheEntryTheRunPromisedToSpare()
    {
        var busy = _temp.CreateDirectory("temp", "live-session");
        Abandoned(8192, "temp", "live-session", "working.txt");

        var provider = CreateProvider(new FakeLiveTreeInspector(busy));
        var plan = await provider.PlanAsync();

        // Standing in for the over-broad rule: the folder is still there and its contents are gone.
        File.Delete(Path.Combine(busy, "working.txt"));

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(busy, StringComparison.OrdinalIgnoreCase)
            && c.Outcome == VerificationOutcome.Emptied);
    }

    /// <summary>
    /// §5.2 on a root that arrives from an environment variable rather than from knowledge. A
    /// <c>%TEMP%</c> pointing at a drive root would turn a clean into the loss of the volume.
    /// </summary>
    [Fact]
    public async Task RefusesATemporaryFolderRedirectedToADriveRoot()
    {
        _environment.WithEnvironmentVariable("TEMP", @"C:\");
        Abandoned(1024, "temp", "old.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([UserTemp], plan.Steps.OfType<ClearDirectoryStep>().Select(s => s.Path));
        Assert.DoesNotContain(@"C:\", plan.TargetedPaths);

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("root of a drive", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same refusal for a redirection that holds the machine rather than being it. A variable
    /// pointing one level above the profile satisfies no name check and every containment one.
    /// </summary>
    [Fact]
    public async Task RefusesATemporaryFolderThatHoldsADirectoryWindowsIsBuiltOutOf()
    {
        _environment.WithEnvironmentVariable("TMP", _temp.Path);
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(_temp.Path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("Windows is built out of", StringComparison.Ordinal));
    }

    /// <summary>
    /// The variations in the issue's own words. <c>%TMP%</c> and <c>%TEMP%</c> are allowed to
    /// disagree, and <c>Path.GetTempPath</c> answers with only the first of them that is set — so a
    /// machine configured that way has a scratch folder nothing would ever look at.
    /// </summary>
    [Fact]
    public async Task ReachesASecondTemporaryFolderTheTwoVariablesDisagreeAbout()
    {
        var other = _temp.CreateDirectory("other-temp");
        _environment.WithEnvironmentVariable("TEMP", other);

        Abandoned(1024, "temp", "old.tmp");
        Abandoned(2048, "other-temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [UserTemp, other],
            plan.Steps.OfType<ClearDirectoryStep>().Select(s => s.Path));

        Assert.Equal(1024 + 2048, plan.EstimatedBytes);
    }

    /// <summary>
    /// One folder named twice is one step. The default location and the resolved one are the same
    /// directory on an ordinary machine, and a plan offering it twice would double its own estimate.
    /// </summary>
    [Fact]
    public async Task NamesOneFolderOnceHoweverManySettingsPointAtIt()
    {
        _environment
            .WithEnvironmentVariable("TMP", UserTemp)
            .WithEnvironmentVariable("TEMP", UserTemp + Path.DirectorySeparatorChar);

        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([UserTemp], plan.Steps.OfType<ClearDirectoryStep>().Select(s => s.Path));
        Assert.Equal(1024, plan.EstimatedBytes);
    }

    /// <summary>
    /// The machine's folder belongs to the operating system, so the plan says plainly that it cannot
    /// be cleared by an ordinary run rather than finding out during execution.
    /// </summary>
    [Fact]
    public async Task TheMachinesTemporaryFolderDeclaresThatItNeedsAdministratorRights()
    {
        Abandoned(1024, "temp", "old.tmp");
        Abandoned(2048, "Windows", "Temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.RequiresElevation);
        Assert.False(Assert.Single(plan.Steps, s => s.SelectionKey == UserTemp).RequiresElevation);
        Assert.True(Assert.Single(plan.Steps, s => s.SelectionKey == MachineTemp).RequiresElevation);
    }

    /// <summary>
    /// §9's exclusions, asserted rather than merely not mentioned. A rule reaching into
    /// <c>C:\Windows</c> has to produce evidence that it did not reach the component store or the
    /// installer cache, and the two neighbours are executed against rather than only inspected.
    /// </summary>
    [Fact]
    public async Task TheSection9ExclusionsSurviveARunIntoTheMachinesTemporaryFolder()
    {
        Abandoned(2048, "Windows", "Temp", "old.tmp");

        var winSxS = _temp.CreateDirectory("Windows", "WinSxS");
        var installer = _temp.CreateDirectory("Windows", "Installer");
        _temp.CreateFile(64, "Windows", "WinSxS", "manifest.xml");
        _temp.CreateFile(64, "Windows", "Installer", "patch.msp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var asserted in new[] { _system.WindowsDirectory, winSxS, installer })
        {
            Assert.DoesNotContain(asserted, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(asserted, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(winSxS), "the component store was destroyed");
        Assert.True(Directory.Exists(installer), "the installer cache was destroyed");
        Assert.True(Directory.Exists(MachineTemp), "the machine's temporary folder itself was destroyed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A temporary folder that is itself a junction is declined rather than followed. Deleting
    /// through it would empty a tree nobody classified, and every survivor named for that root
    /// resolves through the link — so the §5.6 negative would pass over the wreckage.
    /// </summary>
    [Fact]
    public async Task DeclinesATemporaryFolderThatTurnedOutToBeALink()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        Abandoned(4096, "elsewhere", "payload.bin");

        var linked = Path.Combine(_temp.Path, "linked-temp");
        Directory.CreateSymbolicLink(linked, outside);
        _environment.WithEnvironmentVariable("TEMP", linked);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(linked, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(outside, "payload.bin")));
    }

    /// <summary>
    /// A check that could not run must not look like a check that found nothing. The user is told,
    /// because a scratch folder cleaned while the check was blind is the one case where the age
    /// filter is carrying the whole decision on its own.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenItCouldNotTellWhetherAnythingWasInUse()
    {
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("could not check", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row states the interval it is applying, and states the one the constant holds. A sentence
    /// quoting a different number from the rule it describes is worse than no sentence.
    /// </summary>
    [Fact]
    public async Task SaysWhichCutOffItIsApplying()
    {
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains($"{(int)TempDirectoryProvider.StaleAfter.TotalDays} days", StringComparison.Ordinal));
    }

    /// <summary>
    /// The floor is Deguffer's decision, so the plan must not attribute it to the user. "As you
    /// asked" under a cut-off nobody chose sends a reader to a settings page to change something
    /// that is not there.
    /// </summary>
    [Fact]
    public async Task DoesNotTellTheUserTheyAskedForTheFloor()
    {
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.Keep.IsOn, "the plan did not carry the floor the removal has to apply");
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("as you asked", StringComparison.Ordinal));

        // And with a guard the user did set, the sentence appears and is about their window.
        var guarded = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Contains(guarded.Notes, n => n.Message.Contains("as you asked", StringComparison.Ordinal));
    }

    /// <summary>
    /// An empty scratch folder reads as already clear, which is the honest rendering: it was
    /// examined and there is nothing in it.
    /// </summary>
    [Fact]
    public async Task AnEmptyTemporaryFolderIsClearRatherThanUnexamined()
    {
        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(0, plan.EstimatedBytes);
        Assert.False(plan.WasNotExamined);
        Assert.False(plan.HasRecentContentHeldBack);
    }
}
