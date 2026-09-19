using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
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

    private TempDirectoryProvider CreateProvider(
        ILiveTreeInspector? liveTrees = null,
        AppPreferences? preferences = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(preferences ?? AppPreferences.Default));

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
            c.Subject.Equals(busy, StringComparison.OrdinalIgnoreCase)
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
    /// §5.2's own rule, applied to a root that arrives from an environment variable: what Deguffer
    /// does not recognise as a temporary folder, it does not empty.
    ///
    /// <para>Containment alone is not enough, and this is the case that shows it. Neither of these
    /// holds a directory Windows is built out of, so every containment test passes them — and a row
    /// labelled "Temporary files" would then delete every file in somebody's Documents folder over
    /// a week old.</para>
    /// </summary>
    [Theory]
    [InlineData("Documents")]
    [InlineData("System32")]
    [InlineData("scratch")]
    public async Task RefusesAFolderNothingAboutWhichSaysItIsTemporary(string name)
    {
        var elsewhere = _temp.CreateDirectory(name);
        Abandoned(4096, name, "letter.docx");

        _environment.WithEnvironmentVariable("TMP", elsewhere);
        Abandoned(1024, "temp", "old.tmp");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([UserTemp], plan.Steps.OfType<ClearDirectoryStep>().Select(s => s.Path));
        Assert.DoesNotContain(elsewhere, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("called Temp or Tmp", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(elsewhere, "letter.docx")),
            $"a folder called {name} was emptied because a setting pointed at it");
    }

    /// <summary>
    /// One folder declined is one sentence, however many settings point at it.
    ///
    /// <para>Found by driving the real app rather than here: a <c>%TMP%</c> Deguffer declines is
    /// also what <c>Path.GetTempPath</c> answers with, so the same folder arrived as two candidates
    /// and the preview named it twice — in the plural, about one setting. Deduplicating only the
    /// folders that were accepted was the gap.</para>
    /// </summary>
    [Fact]
    public async Task NamesARefusedFolderOnceHoweverManySettingsPointAtIt()
    {
        var elsewhere = _temp.CreateDirectory("papers");

        // What the machine does: GetTempPath answers from the variable, so both name one folder.
        _environment.WithTempPath(elsewhere).WithEnvironmentVariable("TMP", elsewhere);
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        var note = Assert.Single(
            plan.Notes, n => n.Message.Contains("will not empty", StringComparison.Ordinal));

        Assert.Equal(
            1,
            note.Message.Split(elsewhere, StringSplitOptions.None).Length - 1);

        Assert.Contains("points a temporary-folder setting", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder Windows itself hands out per session is recognised, so the rule above refuses what
    /// is unknown rather than everything that is not the default location.
    ///
    /// A Remote Desktop host sets <c>%TEMP%</c> to a numbered folder inside the profile's own, which
    /// is why the name is looked for at the folder holding it as well as at the folder itself.
    /// Deeper than that is deliberately not recognised — see <c>TempRoots.IsNamedAsTemporary</c>.
    /// </summary>
    [Fact]
    public async Task RecognisesTheNumberedFolderARemoteDesktopHostHandsOut()
    {
        var session = _temp.CreateDirectory("temp", "2");
        Abandoned(4096, "temp", "2", "old.tmp");

        _environment.WithTempPath(session);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(session, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two temporary folders where one sits inside the other is the pairing that destroys a live
    /// <c>%TEMP%</c>, and a Remote Desktop session host produces it by default.
    ///
    /// <para>The outer step's walk has no idea the inner folder is a target of its own: it is an
    /// ordinary subdirectory to it, so it is emptied and then removed. The folder Windows will not
    /// put back is then gone rather than cleared, which is the whole thing
    /// <see cref="ClearDirectoryStep"/> exists to prevent. §5.6 reports it afterwards, which is
    /// detection rather than prevention.</para>
    ///
    /// <para>The one this process would actually use wins, because <c>Candidates</c> yields it
    /// first.</para>
    /// </summary>
    [Fact]
    public async Task RefusesATemporaryFolderThatNestsWithOneAlreadyAccepted()
    {
        var outer = _temp.CreateDirectory("temp");
        var session = _temp.CreateDirectory("temp", "2");

        Abandoned(4096, "temp", "2", "old.tmp");
        Abandoned(1024, "temp", "beside.tmp");

        // What a session host does: this process resolves to the numbered folder, and the account's
        // own setting still names the folder holding it.
        _environment.WithTempPath(session).WithEnvironmentVariable("TEMP", outer);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(session, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(outer, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("sits inside it", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "a temporary folder was deleted rather than cleared");
        Assert.True(
            File.Exists(Path.Combine(outer, "beside.tmp")),
            "the folder that was refused was cleaned anyway");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The same pair read in the other order comes out the same way, and this is the assertion that
    /// matters most in the file.
    ///
    /// <para><b>Refusing a folder does not protect it.</b> Keeping whichever of a nested pair was
    /// read first leaves the outer one accepted on a machine whose settings name it first — and its
    /// step then meets the inner folder as an ordinary subdirectory, empties it and removes it. The
    /// folder Windows will not put back is gone, and the refusal did nothing about it. So the
    /// innermost wins whatever the order: clearing the inner cannot destroy the outer, and clearing
    /// the outer always destroys the inner.</para>
    ///
    /// <para>Found by a review probe rather than by this file, which had only the pair in the
    /// convenient order.</para>
    /// </summary>
    [Fact]
    public async Task KeepsTheInnerFolderEvenWhenTheOuterOneIsNamedFirst()
    {
        var outer = _temp.CreateDirectory("temp");
        var session = _temp.CreateDirectory("temp", "2");

        var inSession = Abandoned(4096, "temp", "2", "session.tmp");
        Abandoned(1024, "temp", "beside.tmp");

        // Path.GetTempPath prefers TMP, so this is a machine whose process resolves to the outer
        // folder while the other setting names the inner one.
        _environment
            .WithTempPath(outer)
            .WithEnvironmentVariable("TMP", outer)
            .WithEnvironmentVariable("TEMP", session);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(session, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(outer, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(session), "the inner temporary folder was deleted");
        Assert.False(File.Exists(inSession), "the inner folder was not cleared");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A folder underneath <c>C:\Windows\Temp</c> is already covered by the machine's own step, and
    /// declaring it again would offer it without the administrator rights that folder needs.
    /// </summary>
    [Fact]
    public async Task RefusesAFolderInsideTheMachinesOwnTemporaryFolder()
    {
        var inside = _temp.CreateDirectory("Windows", "Temp", "1");
        Abandoned(2048, "Windows", "Temp", "1", "old.tmp");

        _environment.WithEnvironmentVariable("TMP", inside);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(inside, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(MachineTemp, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("temporary folder Windows itself uses", StringComparison.Ordinal));
    }

    /// <summary>
    /// The machine's own folder is declared before any of the account's settings are read, so a
    /// <c>%TEMP%</c> pointing into it cannot be offered a second time — and offered that second time
    /// without the administrator rights the real declaration carries.
    /// </summary>
    [Fact]
    public async Task DoesNotOfferTheMachinesFolderTwiceWhenASettingPointsAtIt()
    {
        Abandoned(2048, "Windows", "Temp", "old.tmp");
        _environment.WithEnvironmentVariable("TMP", MachineTemp);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps, s => s.SelectionKey == MachineTemp);

        Assert.True(step.RequiresElevation);
        Assert.Equal(2048, plan.EstimatedBytes);
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
        var other = _temp.CreateDirectory("second", "Temp");
        _environment.WithEnvironmentVariable("TEMP", other);

        Abandoned(1024, "temp", "old.tmp");
        Abandoned(2048, "second", "Temp", "old.tmp");

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

        var linked = Path.Combine(_temp.CreateDirectory("linked"), "Temp");
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
            && n.Message.Contains("could not check", StringComparison.Ordinal)
            && n.Message.Contains("working in them", StringComparison.Ordinal)
            && !n.Message.Contains("project", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The row states the interval it is applying, and states the one actually in force. A sentence
    /// quoting a different number from the rule it describes is worse than no sentence.
    /// </summary>
    [Fact]
    public async Task SaysWhichCutOffItIsApplying()
    {
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains(
            $"{AppPreferences.Default.MinimumTemporaryFileAgeDays} days", StringComparison.Ordinal));

        // And the number is the setting's rather than a constant of the provider's own.
        var shorter = await CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 2 }).PlanAsync();

        Assert.Contains(shorter.Notes, n =>
            n.Message.Contains("2 days", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7's cost sentence quotes the cut-off in force, not a number fixed when the class was
    /// written.
    ///
    /// <para>It is the sentence the row shows on its face and in its tooltip, so a stale number
    /// here is the user's check on the only safety mechanism this location has, reading false. The
    /// recommendation in the provider's own description states the same thing and is asserted with
    /// it — two sentences on one row disagreeing about the cut-off is worse than neither.</para>
    /// </summary>
    [Theory]
    [InlineData(7, "7 days")]
    [InlineData(2, "2 days")]
    [InlineData(1, "a day")]
    public void StatesTheCutOffInForceOnTheRowItself(int days, string expected)
    {
        var provider = CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = days });

        Assert.Contains(expected, provider.WhatHappensOnNextUse, StringComparison.Ordinal);
        Assert.Contains(expected, provider.Description.Recommendation, StringComparison.Ordinal);

        // It is prose on the face of a row, so it has to read like prose. Splitting the sentence
        // into a helper is what dropped the capital, and no assertion about the number would
        // notice.
        Assert.All(
            Sentences(provider.WhatHappensOnNextUse),
            sentence => Assert.True(
                char.IsUpper(sentence[0]),
                $"a sentence on the row starts in lower case: '{sentence}'"));
    }

    /// <summary>
    /// The row's own sentence cannot see the user's guard, so it must not claim the absence of a
    /// protection that guard may be providing.
    ///
    /// <para>Stated as "no age limit of its own", which is true whether or not the guard is set. The
    /// earlier wording said everything was offered however recently it was written, which the
    /// estimate, the plan's note and the note beside it all contradicted on a machine with the guard
    /// on.</para>
    /// </summary>
    [Fact]
    public void DoesNotClaimNothingIsHeldBackWhenItCannotKnow()
    {
        var provider = CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 });

        Assert.DoesNotContain(
            "however recently it was written",
            provider.WhatHappensOnNextUse,
            StringComparison.Ordinal);

        Assert.Contains("no age limit of its own", provider.WhatHappensOnNextUse, StringComparison.Ordinal);
    }

    /// <summary>The sentences of a paragraph, for an assertion about how each one starts.</summary>
    private static IEnumerable<string> Sentences(string prose) => prose
        .Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// And at zero it says there is none, rather than quoting a window nobody is applying.
    /// </summary>
    [Fact]
    public void SaysOnTheRowItselfWhenThereIsNoCutOff()
    {
        var provider = CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 });

        Assert.Contains("no age limit", provider.WhatHappensOnNextUse, StringComparison.Ordinal);
        Assert.Contains("set to none", provider.Description.Recommendation, StringComparison.Ordinal);

        // The number that would have been wrong is the one that must not appear.
        Assert.DoesNotContain("seven days", provider.WhatHappensOnNextUse, StringComparison.Ordinal);
        Assert.DoesNotContain("a week", provider.Description.Recommendation, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no floor but a guard the user set, recent files are held back after all — so the row
    /// must not carry the warning that says nothing is.
    ///
    /// <para>The warning is chosen on the guard in force rather than on this provider's own
    /// contribution to it. Chosen on the floor alone it contradicted the note
    /// <c>CleanupProviderBase</c> adds a moment later, which correctly says the user's window is
    /// being honoured.</para>
    /// </summary>
    [Fact]
    public async Task DoesNotWarnThatNothingIsHeldBackWhenTheUsersGuardIs()
    {
        _temp.CreateFile(4096, "temp", "being-written.tmp");

        var plan = await CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 })
            .PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.DoesNotContain(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("however recently it was written", StringComparison.Ordinal));

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("guard on recently changed files", StringComparison.Ordinal));
    }

    /// <summary>
    /// Zero is no age limit, and the point of the setting: a file written this second is offered.
    ///
    /// <para>This is the one value on the settings page that removes a safety rule rather than
    /// adjusting one, so it is asserted end to end rather than on the plan's wording — the file the
    /// default would have kept has actually gone by the end of it.</para>
    /// </summary>
    [Fact]
    public async Task OffersEverythingWhenTheAgeLimitIsSetToZero()
    {
        var justWritten = _temp.CreateFile(4096, "temp", "being-written.tmp");

        var provider = CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 });

        var plan = await provider.PlanAsync();

        Assert.Equal(4096, plan.EstimatedBytes);
        Assert.False(plan.Keep.IsOn, "a floor of zero left a guard on the plan");

        var result = await provider.ExecuteAsync(plan);

        Assert.False(File.Exists(justWritten), "the age limit was set to zero and the file survived");
        Assert.Equal(4096, result.BytesReclaimed);
        Assert.True(Directory.Exists(UserTemp), "the folder itself went with its contents");
    }

    /// <summary>
    /// Zero says so on a warning rather than reading as a rule that happens to be zero.
    ///
    /// <para>"Only what nothing has touched for 0 days is offered" is a sentence shaped like a
    /// safeguard describing none, and it would sit on the row in the same grey as the scan-route
    /// note. What survives at zero is named too, because a user who set it should know what is left
    /// rather than assume it is nothing.</para>
    /// </summary>
    [Fact]
    public async Task SaysPlainlyWhenThereIsNoAgeLimitAtAll()
    {
        _temp.CreateFile(4096, "temp", "being-written.tmp");

        var plan = await CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 }).PlanAsync();

        var note = Assert.Single(
            plan.Notes, n => n.Message.Contains("No age limit", StringComparison.Ordinal));

        Assert.Equal(PlanNoteSeverity.Warning, note.Severity);
        Assert.Contains("still left alone", note.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(plan.Notes, n =>
            n.Message.Contains("0 days", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two guards still compose at zero. Setting no floor is not a way to defeat the user's own
    /// guard on recently changed files, which is a separate decision about a different question.
    /// </summary>
    [Fact]
    public async Task AGuardTheUserSetStillAppliesWhenThereIsNoFloor()
    {
        var justWritten = _temp.CreateFile(4096, "temp", "being-written.tmp");

        var provider = CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = 0 });

        var plan = await provider.PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Equal(0, plan.EstimatedBytes);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(justWritten), "no floor let the clean past the user's own guard");
    }

    /// <summary>
    /// A hand-edited <c>preferences.json</c> reaches the provider without passing the settings box,
    /// so the bounds are applied here as well. A negative is zero, and an absurd number is a year.
    /// </summary>
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, TempDirectoryProvider.MaximumStaleDays)]
    public async Task ClampsAnAgeLimitNothingValidatedOnTheWayIn(int configured, int applied)
    {
        Abandoned(1024, "temp", "old.tmp");

        var plan = await CreateProvider(
            preferences: AppPreferences.Default with { MinimumTemporaryFileAgeDays = configured })
            .PlanAsync();

        if (applied == 0)
        {
            Assert.False(plan.Keep.IsOn);
            Assert.Equal(1024, plan.EstimatedBytes);
            return;
        }

        // A year holds back the thirty-day-old file, which is how the upper clamp is observed.
        Assert.True(plan.Keep.IsOn);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// §7's age column is left blank here, and that is a decision rather than an omission.
    ///
    /// <para>The age of a temporary folder is whatever any program on the machine wrote last, which
    /// is seconds ago on every machine for ever. The row would have said "written moments ago"
    /// beside an offer that excludes everything newer than the cut-off — not merely uninformative,
    /// but the opposite of what the row does.</para>
    /// </summary>
    [Fact]
    public async Task ReportsNoAgeForAFolderWhoseAgeWouldAlwaysBeNow()
    {
        Abandoned(4096, "temp", "old.tmp");
        _temp.CreateFile(64, "temp", "written-just-now.tmp");

        var plan = await CreateProvider().PlanAsync();

        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
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

        // And with a guard the user did set, the sentence appears and quotes THEIR window rather
        // than the one actually in force. The floor is stricter, so the two differ — and this is the
        // only provider on which they can, which makes it the only place the mistake is visible.
        var guarded = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        var asked = Assert.Single(
            guarded.Notes, n => n.Message.Contains("as you asked", StringComparison.Ordinal));

        Assert.Contains("8 hours", asked.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("7 days", asked.Message, StringComparison.Ordinal);

        // The floor is still stated, by the provider, on a note of its own.
        Assert.Contains(guarded.Notes, n =>
            n.Message.Contains("nothing has touched for 7 days", StringComparison.Ordinal));
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

    /// <summary>
    /// Issue #117, end to end, with Windows itself refusing. Something below the ACL guarded every
    /// file in browser profiles a test runner had left here: each preview offered them, each clean was
    /// refused and reported them "in use", and the next preview offered the same bytes again.
    ///
    /// <para>The first preview still offers them, and that is the stated limit rather than an
    /// oversight — nothing has asked Windows yet. What must hold is that the clean says what it was
    /// refused, and that the preview after it leaves that out while still offering what has arrived
    /// since. The refused file surviving, and §5.6 passing, are the negative.</para>
    /// </summary>
    [Fact]
    public async Task LeavesOutOfTheNextPreviewWhatWindowsWouldNotLetTheCleanTake()
    {
        var guarded = Abandoned(4096, "temp", "playwright_chromiumdev_profile-TEST", "Default", "Cookies");
        Abandoned(1024, "temp", "abandoned.tmp");

        using var undeletable = new UndeletableFile(guarded);

        var provider = CreateProvider();
        var first = await provider.PlanAsync();

        Assert.Equal(4096 + 1024, first.EstimatedBytes);

        var result = await provider.ExecuteAsync(first);

        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Equal(new RefusalTally(1, 4096), result.Refused.Denied);
        Assert.True(File.Exists(guarded), "the fixture let a guarded file go");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        Abandoned(2048, "temp", "arrived-since.tmp");

        var next = await provider.PlanAsync();

        Assert.Equal(2048, next.EstimatedBytes);
        Assert.True(next.HasRefusedContent);

        var step = Assert.Single(next.Steps.OfType<ClearDirectoryStep>(), s => s.Path == UserTemp);
        Assert.Equal(new RefusalTally(1, 4096), step.Refused.Denied);

        Assert.Contains(next.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("Windows would not let Deguffer remove", StringComparison.Ordinal)
            && n.Message.Contains("playwright_chromiumdev_profile-TEST", StringComparison.Ordinal));
    }

    /// <summary>
    /// The record says where to look, never what the answer was. Once Windows stops refusing, the very
    /// next preview counts the bytes again — without waiting for a clean that a row measuring zero
    /// would never have offered.
    /// </summary>
    [Fact]
    public async Task CountsItAgainAsSoonAsWindowsStopsRefusing()
    {
        var guarded = Abandoned(4096, "temp", "profile", "Default", "Cookies");

        var undeletable = new UndeletableFile(guarded);

        var provider = CreateProvider();

        try
        {
            await provider.ExecuteAsync(await provider.PlanAsync());
            Assert.Equal(0, (await provider.PlanAsync()).EstimatedBytes);
        }
        finally
        {
            undeletable.Dispose();
        }

        var lifted = await provider.PlanAsync();

        Assert.Equal(4096, lifted.EstimatedBytes);
        Assert.False(lifted.HasRefusedContent);
        Assert.DoesNotContain(lifted.Notes, n => n.Message.Contains("would not let Deguffer", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file another program holds open is the other refusal, and it is said as that: the reader
    /// answers it by closing the program, and the note is information rather than a warning.
    /// </summary>
    [Fact]
    public async Task LeavesOutWhatAnotherProgramStillHoldsOpen()
    {
        var state = Abandoned(2048, "temp", "editor-session", "state.db");
        Abandoned(1024, "temp", "abandoned.tmp");

        var provider = CreateProvider();
        var first = await provider.PlanAsync();

        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await provider.ExecuteAsync(first);

            Assert.Equal(new RefusalTally(1, 2048), result.Refused.InUse);
            Assert.Equal(default, result.Refused.Denied);

            var next = await provider.PlanAsync();

            Assert.Equal(0, next.EstimatedBytes);
            Assert.Contains(next.Notes, n =>
                n.Severity == PlanNoteSeverity.Information
                && n.Message.Contains("Another program had", StringComparison.Ordinal)
                && n.Message.Contains("editor-session", StringComparison.Ordinal));
            Assert.DoesNotContain(next.Notes, n => n.Message.Contains("would not let Deguffer", StringComparison.Ordinal));
        }

        Assert.Equal(2048, (await provider.PlanAsync()).EstimatedBytes);
    }

    /// <summary>
    /// §7.1 over an entry a running program is working in, which is §5.3's case exactly. This
    /// provider declares no root, and everything inside a scratch folder is ordinary to Explore, so
    /// only a declaration that asks what is running keeps the live entry from being offered.
    ///
    /// <para>The scratch folder is inside the profile here, where it is on a real machine. The
    /// fixture's default sits beside the profile, where the region table refuses everything as
    /// another account's, and this test would pass with no declaration at all.</para>
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAnEntryARunningProgramIsUsing()
    {
        _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp"));

        var busy = Path.Combine(UserTemp, "live-session");
        var idle = Path.Combine(UserTemp, "abandoned");
        Abandoned(8192, "profile", "AppData", "Local", "Temp", "live-session", "working.txt");
        Abandoned(1024, "profile", "AppData", "Local", "Temp", "abandoned", "old.tmp");

        var provider = CreateProvider(new FakeLiveTreeInspector(busy));
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, [provider]);

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(busy, StringComparison.OrdinalIgnoreCase));

        Assert.False(policy.MayRemove(busy).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(busy, "working.txt")).IsAllowed);
        Assert.True(policy.MayRemove(idle).IsAllowed);
    }

    /// <summary>
    /// §7.1 over a scratch folder Windows will not describe. Nothing could ask which of its entries a
    /// running program is working in, so Explore refuses all of them rather than none.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAllOfAScratchFolderWindowsWillNotDescribe()
    {
        _environment.WithTempPath(Path.Combine(_environment.LocalAppData, "Temp"));

        var idle = Path.Combine(UserTemp, "abandoned");
        Abandoned(1024, "profile", "AppData", "Local", "Temp", "abandoned", "old.tmp");

        using var denied = DeniedDirectory.WithUnreadableAttributes(UserTemp);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, [provider]);

        Assert.True(plan.HasUnreadableRoot);
        Assert.False(policy.MayRemove(idle).IsAllowed);
    }
}
