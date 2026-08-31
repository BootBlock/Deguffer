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

    public RecycleBinProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Sid => _environment.UserSecurityIdentifier!;

    private RecycleBinProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, volumes: _volumes);

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
        Assert.False(Directory.Exists(mine));
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
    /// §7: Tier 3 requires typed confirmation. This is the first provider to reach that path, so
    /// the wiring from the provider's tier through to the phrase the shell asks for is worth
    /// pinning end to end.
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
    /// §6.3. A bin is flat in normal use, but it holds whatever the user deleted — and a deleted
    /// directory keeps its whole tree, which is how a path past <c>MAX_PATH</c> gets in here. A
    /// truncation would be a partial deletion of something already irreversible.
    ///
    /// A crash guard rather than a discriminating test, on the reasoning
    /// <c>docs/todo/after-the-scanner.md</c> records: .NET prefixes long paths itself, so an
    /// outcome-based check passes even with <see cref="LongPath.Extended"/> removed.
    /// <c>DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm</c> is what
    /// actually proves the form.
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

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(bin, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.EstimatedBytes > 0, "a deleted tree past MAX_PATH was not measured.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.FileExists(file), "a file past MAX_PATH survived the removal.");
        Assert.False(Directory.Exists(bin));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }
}
