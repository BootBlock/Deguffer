using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first Tier 3 provider, and the first that works per volume rather than per profile.
///
/// §5.2 is sharper here than anywhere else in the suite: the child that must be spared and the
/// child that must go are siblings of identical shape under one parent, told apart by nothing but
/// which account's identifier they carry — and the spared one holds another person's deleted files.
/// Most of what follows is therefore a negative test.
/// </summary>
public sealed class RecycleBinProviderTests : IDisposable
{
    /// <summary>Recognisably invented, and shaped like the identifiers Windows actually writes.</summary>
    private const string AnotherAccount = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private const string LocalSystem = "S-1-5-18";

    private const string BinName = "$Recycle.Bin";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    /// <summary>
    /// Never the real one. The shipped route asks Windows to empty the bin, so a fixture that let
    /// the default through would empty the Recycle Bin of whoever ran the suite.
    /// </summary>
    private FakeRecycleBinEmptier _emptier = new();

    public RecycleBinProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Sid => _environment.UserSecurityIdentifier!;

    private RecycleBinProvider CreateProvider(
        AppPreferences? preferences = null,
        FakeRecycleBinEmptier? emptier = null)
    {
        _emptier = emptier ?? _emptier;

        return new RecycleBinProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            volumes: _volumes,
            preferences: new FakePreferences(preferences ?? AppPreferences.Default),
            emptier: _emptier);
    }

    /// <summary>A volume root under the scratch tree, registered as fixed and ready.</summary>
    private string CreateVolume(string name, DriveType kind = DriveType.Fixed, bool isReady = true)
    {
        var root = _temp.CreateDirectory("volumes", name);
        _volumes.With(root, kind, isReady);
        return root;
    }

    /// <summary>One account's bin on <paramref name="volumeRoot"/>, holding a deleted file.</summary>
    private static string CreateBin(string volumeRoot, string sid, int bytes = 4096)
    {
        var directory = Path.Combine(volumeRoot, BinName, sid);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "$RA1B2C3.txt"), new byte[bytes]);
        File.WriteAllBytes(Path.Combine(directory, "$IA1B2C3.txt"), new byte[544]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoVolumeHoldsABinForThisUser()
    {
        CreateVolume("C");
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// A bin root exists on every Windows volume, so reading its existence as presence would report
    /// this source on every machine and then plan nothing on most of them. Presence is this user's
    /// own bin — the same rule the shader caches needed for a vendor directory.
    /// </summary>
    [Fact]
    public async Task ABinHoldingOnlyOtherAccountsIsNotPresence()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, AnotherAccount);

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansOneStepPerVolumeThatHoldsThisUsersBin()
    {
        var c = CreateVolume("C");
        var d = CreateVolume("D");
        CreateVolume("G");

        var onC = CreateBin(c, Sid, 8192);
        var onD = CreateBin(d, Sid, 2048);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([onC, onD], plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Equal(SafetyTier.UserData, plan.Tier);
    }

    /// <summary>
    /// §7 makes age a first-class column, and a per-volume row is exactly the grain it was scoped
    /// to. A bin's own timestamp moves whenever an entry arrives or leaves, which is the "you last
    /// deleted something on this drive eight months ago" the user decides on.
    /// </summary>
    [Fact]
    public async Task EveryStepCarriesAnAgeBecauseTheRowIsOneVolume()
    {
        var bin = CreateBin(CreateVolume("D"), Sid);
        var written = DateTime.UtcNow.AddDays(-200);
        Directory.SetLastWriteTimeUtc(bin, written);

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        Assert.NotNull(step.LastWritten);

        // Against the value written, rather than a window around "about 200 days ago" that any
        // roughly-right timestamp would satisfy. The comparison subtracts ticks and ignores
        // DateTimeKind, so it says nothing about which Kind the provider returns — and it does not
        // need to. RelativeAge.Describe is the only consumer and normalises both sides itself.
        Assert.Equal(written, step.LastWritten!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task NeverTargetsABinRootItself()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var root = Path.Combine(volume, BinName);
        Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(provider.BinRoots, r => r.Equals(root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// §5.2's dangerous direction, in its sharpest form on this repository: another account's bin
    /// is a sibling of the target, identical in shape, and holds files that are not this user's to
    /// destroy. It must be Tier 4, named to the user, asserted to survive, and still there
    /// afterwards.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsBinIsTier4AndSurvivesTheRun()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);
        var theirs = CreateBin(volume, AnotherAccount);
        var system = CreateBin(volume, LocalSystem);
        var stray = Directory.CreateDirectory(Path.Combine(volume, BinName, "not-a-sid")).FullName;

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([mine], plan.TargetedPaths);

        foreach (var spared in new[] { theirs, system, stray })
        {
            Assert.DoesNotContain(spared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(spared, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        Assert.Contains(plan.Notes, n => n.Message.Contains(AnotherAccount, StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);

        // Emptied, not removed. Windows leaves the account's own directory standing and takes its
        // contents, which was observed rather than assumed — see ShellRecycleBinEmptier.
        Assert.True(Directory.Exists(mine));
        Assert.Empty(Directory.EnumerateFileSystemEntries(mine));

        Assert.True(Directory.Exists(theirs), "another account's deleted files were destroyed");
        Assert.True(Directory.Exists(system), "the system account's bin was destroyed");
        Assert.True(Directory.Exists(stray), "an unrecognised directory in the bin root was destroyed");
        Assert.True(Directory.Exists(Path.Combine(volume, BinName)), "the bin root was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// With no identity to match against, every bin on the machine belongs to somebody this
    /// provider cannot name. Recognising none of them is the only reading of §5.2 available, and
    /// the message has to say why rather than reporting each one as merely unrecognised.
    /// </summary>
    [Fact]
    public async Task FailsClosedWhenTheAccountCannotBeIdentified()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);

        // Set before the provider is constructed: the identity is read once, because a process
        // cannot change the account it runs as.
        _environment.WithNoSecurityIdentifier();

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("account", StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(mine), "a bin was emptied although the account was unidentifiable");
    }

    /// <summary>
    /// A bin root is reached by name rather than through an enumeration, so it is the one place a
    /// junction is still walked through. The far side hands back ordinary directories, one of which
    /// could carry this user's identifier — and every survivor named for that volume resolves
    /// through the link and passes, which is the vacuous negative the shader caches met first.
    /// </summary>
    [Fact]
    public async Task AJunctionedBinRootIsNeverLookedThrough()
    {
        var volume = CreateVolume("D");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, Sid));
        var bystander = Path.Combine(outside, Sid, "irreplaceable.bin");
        File.WriteAllBytes(bystander, new byte[4096]);

        Directory.CreateSymbolicLink(Path.Combine(volume, BinName), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        // Nothing targeted and something declined, so the row must not read "Already clear".
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "planning looked through a junctioned bin root");
    }

    /// <summary>
    /// A link inside the bin root is a child the user can see, so a plan that neither offers it nor
    /// mentions it disagrees with the folder. It is never followed.
    /// </summary>
    [Fact]
    public async Task AJunctionedBinIsNamedRatherThanDroppedSilently()
    {
        var volume = CreateVolume("D");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "deleted.bin"), new byte[4096]);

        Directory.CreateDirectory(Path.Combine(volume, BinName));
        Directory.CreateSymbolicLink(Path.Combine(volume, BinName, Sid), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(Sid, StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "deleted.bin")), "a junctioned bin was deleted through");
    }

    /// <summary>
    /// Fixed volumes only. A network share deletes outright rather than to a bin, so a
    /// <c>$RECYCLE.BIN</c> found on one belongs to the server's users; removable media can be
    /// swapped between the preview and the clean; and an unready drive cannot be read at all.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.Network, true)]
    [InlineData(DriveType.CDRom, true)]
    [InlineData(DriveType.Fixed, false)]
    public async Task OnlyAFixedReadyVolumeIsEvenLookedAt(DriveType kind, bool isReady)
    {
        var volume = CreateVolume("X", kind, isReady);
        var bin = CreateBin(volume, Sid);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.BinRoots);

        var plan = await provider.PlanAsync();
        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(bin));
    }

    /// <summary>
    /// The tier is the whole product here (§3), and the declaration has to agree with the tier the
    /// provider claims. A plan carries the provider's tier rather than the child's, and
    /// <see cref="SafetyTierExtensions.IsOfferable"/> admits Tier 1, 2 and 3 alike — so a child
    /// declared at Tier 1 would still be targeted, under a plan still marked Tier 3, and nothing
    /// downstream would notice the declaration disagreeing with the stakes it records.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();
        var declared = provider.DisposableChildren.DisposableNames.ToList();

        // One entry, and it is this account's own identifier. A second would mean the provider had
        // learned to empty something it never decided to.
        Assert.Equal([Sid], declared);
        Assert.All(declared, name =>
            Assert.Equal(provider.Tier, provider.DisposableChildren.Classify(name).Tier));
    }

    /// <summary>
    /// §7's typed phrase, which Tier 3 asks for wherever the user has left that setting on. This is
    /// the first provider to reach that path, so the wiring from the provider's tier through to the
    /// phrase the shell asks for is worth pinning end to end.
    /// </summary>
    [Fact]
    public async Task ATier3PlanDemandsTheTypedPhrase()
    {
        CreateBin(CreateVolume("D"), Sid);

        var provider = CreateProvider();

        Assert.Equal(SafetyTier.UserData, provider.Tier);

        var plan = await provider.PlanAsync();
        var requirement = ConfirmationRequirement.For(plan);

        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Equal(provider.Name, requirement.RequiredPhrase);
        Assert.False(requirement.IsSatisfiedBy([new Confirmation(provider.Id)]));
        Assert.True(requirement.IsSatisfiedBy([new Confirmation(provider.Id, provider.Name)]));
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfABinRootVanished()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The over-broad rule §5.6 exists to catch: the target's parent went with it.
        var root = Path.Combine(volume, BinName);
        Directory.Delete(root, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The volume list is remembered for the life of a pass (G4), so a drive mounted while the app
    /// was open has to be picked up on the next preview like every other cached view of the machine.
    /// </summary>
    [Fact]
    public void InvalidatingReachesTheVolumeList()
    {
        CreateProvider().InvalidateCaches();

        Assert.Equal(1, _volumes.InvalidateCount);
        Assert.Equal(1, _environment.InvalidateCount);
    }

    /// <summary>
    /// The shipped route: Windows is asked to empty the bin, and what it is asked about is the
    /// <em>volume</em> even though the plan names the account's directory inside it.
    ///
    /// <para>Both halves matter. A step of the wrong type would delete the files instead, silently
    /// taking the setting's other side; and a volume root derived wrongly would hand
    /// <c>SHEmptyRecycleBin</c> a path whose reach nobody has established. The second is the
    /// dangerous one, because the call reports success either way.</para>
    /// </summary>
    [Fact]
    public async Task EmptiesThroughWindowsByDefaultAndNamesTheVolume()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.IsType<EmptyRecycleBinStep>(Assert.Single(plan.Steps));
        Assert.Equal(mine, step.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(volume, step.VolumeRoot, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal([volume], _emptier.VolumeRoots);
        Assert.Empty(Directory.EnumerateFileSystemEntries(mine));
    }

    /// <summary>
    /// What actually crosses to Windows on a real plan, in the form the shell accepts.
    ///
    /// <para><b>It does not discriminate either strip on its own, and saying so is the point.</b>
    /// Two independent calls put the path in display form — the provider's own
    /// <see cref="LongPath.Display"/> on each child it classifies, and
    /// <see cref="EmptyRecycleBinStep.VolumeRoot"/>'s — so removing either leaves this green and
    /// only removing both fails it. That redundancy is worth having, and this test is worth having
    /// as proof of the composition, but §6.3's discriminating check on this seam is
    /// <see cref="TheVolumeHandedToWindowsIsStrippedOfTheExtendedLengthPrefix"/>, which feeds the
    /// step a prefixed path directly.</para>
    /// </summary>
    [Fact]
    public async Task WhatCrossesToWindowsCarriesNoExtendedLengthPrefix()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);

        var provider = CreateProvider();
        await provider.ExecuteAsync(await provider.PlanAsync());

        var handed = Assert.Single(_emptier.VolumeRoots);
        Assert.False(handed.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.Equal(LongPath.Display(handed), handed);
    }

    /// <summary>
    /// The setting takes the other side: the files go directly, Windows is never asked, and the
    /// account's directory is removed rather than left standing.
    /// </summary>
    [Fact]
    public async Task TheDirectSettingRemovesTheFilesAndNeverAsksWindows()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);

        var provider = CreateProvider(AppPreferences.Default with { EmptyRecycleBinsDirectly = true });
        var plan = await provider.PlanAsync();

        Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Empty(_emptier.VolumeRoots);
        Assert.False(Directory.Exists(mine));
        Assert.True(Directory.Exists(Path.Combine(volume, BinName)), "the bin root was removed");
    }

    /// <summary>
    /// The guard outranks the setting, and it has to: Windows empties a bin whole and offers no way
    /// to hold anything back, so a plan whose estimate already excludes a recent file must not then
    /// hand the bin to something that will take it. The user is told, because a route chosen for
    /// them against their setting is not something to leave unsaid.
    /// </summary>
    [Fact]
    public async Task TheGuardOnRecentFilesForcesTheDirectRouteAndSaysSo()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Windows empties a bin whole", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.Empty(_emptier.VolumeRoots);
    }

    /// <summary>
    /// A step's key is what a choice the user made is matched against on the next scan, so the two
    /// routes have to agree on it. They do because both are deletions keyed on the path — and if
    /// they ever stopped, somebody who changed the setting would find every bin they had ticked
    /// silently unticked, which is the direction that loses a decision rather than a file.
    /// </summary>
    [Fact]
    public async Task ChangingTheRouteKeepsTheKeyTheUsersChoiceIsMatchedOn()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);

        var throughWindows = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        var directly = Assert.Single(
            (await CreateProvider(AppPreferences.Default with { EmptyRecycleBinsDirectly = true })
                .PlanAsync()).Steps);

        Assert.NotEqual(throughWindows.GetType(), directly.GetType());
        Assert.Equal(throughWindows.SelectionKey, directly.SelectionKey);
    }

    /// <summary>
    /// §5.6's whole purpose, against this route specifically. The call names a volume rather than an
    /// account, so an emptying that reached every account's bin on that volume would satisfy every
    /// assertion that the target was emptied — and the only thing that catches it is the negative.
    ///
    /// <para>This is the shape the real call was measured not to have. The test holds the check
    /// rather than the measurement: it proves that if Windows ever did behave this way, Deguffer
    /// would report it rather than call the run a success.</para>
    /// </summary>
    [Fact]
    public async Task AnEmptyThatReachedEveryAccountFailsTheNegative()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid);
        var theirs = CreateBin(volume, AnotherAccount);

        var provider = CreateProvider(emptier: FakeRecycleBinEmptier.TakingEveryAccount());
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        // The loss, in the shape this route's over-reach actually takes: the other account's files
        // are gone and their directory is still standing. An assertion that the directory had gone
        // would be testing a shape Windows does not have, and would pass while this one fails.
        Assert.True(Directory.Exists(theirs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(theirs));

        Assert.False(result.Verification!.Passed);
        Assert.Contains(
            result.Verification.Checks,
            c => c.Path.Equals(theirs, StringComparison.OrdinalIgnoreCase)
                && c.Outcome == VerificationOutcome.Emptied);

        // The one-line summary has to account for it too. A run that reports "did not pass" over a
        // sentence saying every path survived tells the reader two different things at once.
        Assert.Contains(result.Verification.Failures, c =>
            c.Path.Equals(theirs, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("did not survive", result.Verification.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6 across a selection, which is where a per-volume row makes the negative do real work.
    ///
    /// <para>A user who ticks one drive's bin and leaves another's alone has said, in as many words,
    /// that the second must survive. <see cref="CleanupPlan.NarrowedTo"/> turns that into a
    /// protected path for exactly this reason: the declined bin and the selected one are siblings in
    /// shape, so a rule that reached too far takes both. With a route that empties in place, the
    /// declined bin is still standing afterwards however far the call reached, so the check has to
    /// be about what it holds.</para>
    /// </summary>
    [Fact]
    public async Task ABinTheUserDeclinedIsReportedWhenTheCallEmptiesItAnyway()
    {
        var c = CreateVolume("C");
        var d = CreateVolume("D");
        var mine = CreateBin(c, Sid);
        var declined = CreateBin(d, Sid);

        var provider = CreateProvider(emptier: FakeRecycleBinEmptier.AlsoEmptying(declined));
        var plan = await provider.PlanAsync();

        var chosen = plan.Steps.Single(s => ((DeleteStep)s).Path.Equals(mine, StringComparison.OrdinalIgnoreCase));
        var narrowed = plan.NarrowedTo([chosen]);

        var result = await provider.ExecuteAsync(narrowed);

        // The loss: still standing, and everything the user kept has gone out of it.
        Assert.True(Directory.Exists(declined));
        Assert.Empty(Directory.EnumerateFileSystemEntries(declined));

        Assert.False(result.Verification!.Passed);
        Assert.Contains(
            result.Verification.Checks,
            v => v.Path.Equals(declined, StringComparison.OrdinalIgnoreCase)
                && v.Outcome == VerificationOutcome.Emptied);
    }

    /// <summary>
    /// Windows refuses for reasons this code cannot fix — a bin it will not read, a volume with the
    /// bin switched off — and the number it refused with is the only thing that tells them apart. A
    /// refusal has to reach the user as a failed step saying so, never as a quiet success over a bin
    /// that is still full.
    /// </summary>
    [Fact]
    public async Task ARefusalFromWindowsIsReportedAndNothingIsClaimed()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);

        var provider = CreateProvider(
            emptier: FakeRecycleBinEmptier.Refusing("Windows would not empty this Recycle Bin (0x80004005)."));

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var step = Assert.Single(result.Steps);
        Assert.False(step.Succeeded);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Contains("0x80004005", step.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(mine));
    }

    /// <summary>
    /// The ordinary success, end to end: the bin is emptied, everything it held counts as
    /// reclaimed, and the sentence says so plainly.
    ///
    /// <para>It cannot tell a measured reclaim from a reported estimate, because on a bin that is
    /// wholly emptied the two are the same number.
    /// <see cref="WindowsClaimingSuccessOverAFullBinIsAFailedStep"/> is where they differ, and it is
    /// what holds the reclaim to the disk.</para>
    /// </summary>
    [Fact]
    public async Task ReportsTheWholeBinAsReclaimedWhenWindowsEmptiesIt()
    {
        var volume = CreateVolume("D");
        CreateBin(volume, Sid, bytes: 8192);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.EstimatedBytes > 8000);

        var result = await provider.ExecuteAsync(plan);
        var step = Assert.Single(result.Steps);

        // The estimate was over 8 KB and the fake emptied it, so a step reporting the estimate and
        // a step reporting the measurement are indistinguishable here — except that the estimate
        // counts the $I file the emptying also took. Both figures leaving the disk is the point.
        Assert.True(step.Succeeded);
        Assert.Equal(plan.EstimatedBytes, step.BytesReclaimed);
        Assert.Equal("Emptied.", step.Message);
    }

    /// <summary>
    /// Windows reporting success is not evidence that anything left the disk, and this is the case
    /// where the two disagree: <c>SHEmptyRecycleBin</c> returns <c>S_OK</c> and the bin is exactly
    /// as full as it was.
    ///
    /// <para>The route was taken knowing the call reports one number and no figures, so the
    /// measurement afterwards is the only check there is on it. Repeating the shell's claim over the
    /// top of a measurement that contradicts it would throw away the one thing that check is for —
    /// and it is a claim the user acts on, since they are told the bin is dealt with.</para>
    /// </summary>
    [Fact]
    public async Task WindowsClaimingSuccessOverAFullBinIsAFailedStep()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid, bytes: 8192);

        var provider = CreateProvider(emptier: FakeRecycleBinEmptier.DoingNothing());
        var result = await provider.ExecuteAsync(await provider.PlanAsync());
        var step = Assert.Single(result.Steps);

        Assert.False(step.Succeeded);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.DoesNotContain("held nothing", step.Message, StringComparison.Ordinal);
        Assert.Contains("still holds", step.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(mine));
    }

    /// <summary>
    /// The guard and this route are mutually exclusive, and the provider is what makes them so.
    /// This drives the pairing the provider will not build, because the cost of that one expression
    /// being edited wrongly is the files the user asked to keep — and §5.6 could not report it,
    /// since those files sit inside the target rather than beside it.
    /// </summary>
    [Fact]
    public async Task AGuardedPlanIsRefusedByTheShellRouteRatherThanEmptied()
    {
        var volume = CreateVolume("D");
        var mine = CreateBin(volume, Sid);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The pairing the provider never produces, assembled by hand.
        var guarded = plan with { Keep = MinimumAge.WithinHours(8, DateTime.UtcNow) };
        Assert.IsType<EmptyRecycleBinStep>(Assert.Single(guarded.Steps));

        var result = await provider.ExecuteAsync(guarded);

        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.Empty(_emptier.VolumeRoots);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(mine));
    }

    /// <summary>
    /// A step whose path is not shaped like a bin has no volume to name, and the value that reaches
    /// the shell is what decides what the shell destroys. The dangerous shape is a path that is
    /// already a drive root: the derivation cannot go two levels up from it, and a fallback to the
    /// path itself would have handed Windows a whole volume.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    public async Task AStepThatNamesNoVolumeIsRefusedRatherThanGuessedAt(string path)
    {
        Assert.Equal(string.Empty, new EmptyRecycleBinStep(path, "malformed").VolumeRoot);

        var provider = CreateProvider();
        var plan = (await provider.PlanAsync()) with
        {
            Steps = [new EmptyRecycleBinStep(path, "malformed")],
        };

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.Empty(_emptier.VolumeRoots);
    }

    /// <summary>
    /// §6.3 at the one seam in Core that requires the opposite form from every other: the shell
    /// namespace parses a drive root and refuses the extended-length spelling of it.
    ///
    /// <para>Asked of the step directly, and given a path that <em>does</em> carry the prefix, which
    /// is what makes it discriminate. Driven through the provider it would not: the path a plan
    /// carries has already been through <see cref="LongPath.Display"/> in the provider, so the strip
    /// here and the strip there each cover for the other and removing either alone changes
    /// nothing.</para>
    /// </summary>
    [Fact]
    public void TheVolumeHandedToWindowsIsStrippedOfTheExtendedLengthPrefix()
    {
        var step = new EmptyRecycleBinStep(
            LongPath.Extended(@"C:\$Recycle.Bin\" + AnotherAccount), "a bin");

        Assert.StartsWith(@"\\?\", step.Path, StringComparison.Ordinal);
        Assert.Equal(@"C:\", step.VolumeRoot);
    }

    /// <summary>
    /// §6.3. A bin is flat in normal use, but it holds whatever the user deleted — and a deleted
    /// directory keeps its whole tree, which is how a path past <c>MAX_PATH</c> gets in here. A
    /// truncation would be a partial deletion of something already irreversible.
    ///
    /// A crash guard rather than a discriminating test, on the reasoning
    /// <c>docs/todo/after-the-scanner.md</c> records: .NET prefixes long paths itself, so an
    /// outcome-based check passes even with <see cref="LongPath.Extended"/> removed.
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c> is what
    /// actually proves the form.
    ///
    /// <para><b>Driven down the direct route deliberately, because that is the one this test can
    /// still say anything about.</b> The shipped route hands the emptying to Windows, and in a test
    /// that means handing it to <see cref="FakeRecycleBinEmptier"/> — whose own deep-tree handling
    /// would then be what the assertion below exercised. Selecting the direct route puts
    /// <see cref="Execution.DirectoryRemover"/> back under it. The measurement half is route-
    /// independent and would hold either way.</para>
    /// </summary>
    [Fact]
    public async Task MeasuresAndRemovesContentPastMaxPath()
    {
        // Built without CreateBin's shallow entries, so the measured size is evidence about the deep
        // tree alone. With 4 KB of ordinary content beside it, a threshold this assertion could
        // clear on those bytes would pass even if everything past MAX_PATH went unmeasured.
        var bin = Path.Combine(CreateVolume("D"), BinName, Sid);
        Directory.CreateDirectory(bin);

        var deep = Path.Combine(bin, "$RDEEP01");
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var file = Path.Combine(deep, "recovered.bin");
        Assert.True(file.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(file), new byte[4096]);

        var provider = CreateProvider(AppPreferences.Default with { EmptyRecycleBinsDirectly = true });
        var plan = await provider.PlanAsync();

        Assert.Contains(bin, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "a deleted tree past MAX_PATH was not measured.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(file), "a file past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(bin));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The sharpest case of the refusal, because <c>$Recycle.Bin</c> is the one root in the codebase
    /// whose permissions genuinely vary from machine to machine.
    ///
    /// <para>Presence is decided by probing this user's own bin <em>by full name</em>, and a full
    /// path still resolves through a directory the account may not list — listing and traversing are
    /// separate rights. So the provider can answer "present, your bin is on D:" and then, one method
    /// later, enumerate the bin root, be refused, see no children, and conclude "no volume on this
    /// machine holds a Recycle Bin for this user". One pass, two contradictory statements, and the
    /// second one is what the user reads.</para>
    /// </summary>
    [Fact]
    public async Task ABinRootThatWillNotBeListedIsSaidSoRatherThanReportedAsAbsent()
    {
        var volume = CreateVolume("D");
        var bin = CreateBin(volume, Sid);
        var binRoot = Path.Combine(volume, BinName);

        using var denied = new DeniedDirectory(binRoot);

        var provider = CreateProvider();

        // The premise: the by-name probe still finds this user's bin through the refused parent.
        Assert.True(await provider.IsPresentAsync());
        Assert.True(LongPath.DirectoryExists(bin));

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(binRoot));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("No volume on this machine holds"));
        Assert.Empty(plan.TargetedPaths);
    }
}
