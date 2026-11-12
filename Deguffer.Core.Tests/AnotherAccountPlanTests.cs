using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a planning pass withholds while Deguffer runs as an account that is not the one signed in to
/// Windows: the steps that would reach that account's folders, its folder in a Recycle Bin, or its own
/// state through a call, and nothing the whole machine shares.
///
/// <para>Driven against plans built here for the rule, and through the planner and a run for the
/// Recycle Bin, which is the row where the wrong account means another person's deleted files.</para>
/// </summary>
public sealed class AnotherAccountPlanTests : IDisposable
{
    private const string BinName = "$Recycle.Bin";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public AnotherAccountPlanTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private static CleanupPlan Planning(params CleanupStep[] steps) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
    };

    private CleanupPlan Apply(CleanupPlan plan) =>
        AnotherAccountPlan.For(RunningAccount.Of(_environment), _volumes)!.Apply(plan);

    /// <summary>A folder holding one file, so the content Deguffer protects is there to protect.</summary>
    private string Holding(params string[] parts)
    {
        var folder = Path.Combine([.. parts]);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "entry.bin"), new byte[4096]);
        return folder;
    }

    /// <summary>A folder the whole machine shares, outside every folder the fake account owns.</summary>
    private string Machine(string name) => Holding(_temp.Path, "machine", name);

    [Fact]
    public void TheSignedInUsersOwnAccountHasNothingToCheck()
    {
        Assert.True(RunningAccount.Of(_environment).IsSignedInUser);
        Assert.Null(AnotherAccountPlan.For(RunningAccount.Of(_environment), _volumes));
    }

    /// <summary>
    /// The profile's caches and temporary files are the running account's, and are withheld with each
    /// one protected on its content, as a step the user declined is. A step in a folder the whole
    /// machine shares is left as the provider planned it.
    /// </summary>
    [Fact]
    public void AStepInTheRunningAccountsFoldersIsWithheldAndAMachineStepIsKept()
    {
        _environment.ElevatedAsAnotherAccount();
        var cache = Holding(_environment.LocalAppData, "Tool", "Cache");
        var temporary = Holding(_environment.TempPath, "build-1");
        var shared = Machine("Windows-Temp");

        var inProfile = new DeleteDirectoryStep(cache, "Tool cache") { Estimated = new ScanSize(4096, 4096) };
        var inTemp = new DeleteDirectoryStep(temporary, "Temporary files") { Estimated = new ScanSize(4096, 4096) };
        var machine = new DeleteDirectoryStep(shared, "Machine temporary files") { Estimated = new ScanSize(4096, 4096) };

        var applied = Apply(Planning(inProfile, machine, inTemp));

        Assert.Equal([machine], applied.Steps);
        Assert.True(applied.HoldsAnotherAccountsFiles);

        foreach (var withheld in new[] { cache, temporary })
        {
            var protection = Assert.Single(applied.ProtectedPaths, p => p.Path == withheld);
            Assert.Equal(Withholding.AnotherAccount, protection.Withheld);
            Assert.Equal(PathPresence.Present, protection.PresenceBefore);
            Assert.True(protection.HeldContentBefore);
        }

        Assert.DoesNotContain(applied.ProtectedPaths, p => p.Path == shared);

        var note = Assert.Single(applied.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains("2 items here belong", note.Message, StringComparison.Ordinal);
        Assert.Contains($@"{FakeUserEnvironment.Domain}\{FakeUserEnvironment.Account}", note.Message, StringComparison.Ordinal);
        Assert.Contains(FakeUserEnvironment.SignedInName, note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool's own command works where the plan measured it, and a tool run as this account clears this
    /// account's cache, so a command measured in the profile is withheld though it names no subject.
    /// </summary>
    [Fact]
    public void ACommandWorkingInTheRunningAccountsFoldersIsWithheld()
    {
        _environment.ElevatedAsAnotherAccount();
        var command = new RunCommandStep("npm.cmd", "cache clean --force", "Clear npm's cache")
        {
            MeasuredPaths = [Holding(_environment.LocalAppData, "npm-cache")],
            WhenCancelled = CancelledCommand.Finishes,
        };
        var machine = new RunCommandStep("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", "Clean the component store")
        {
            MeasuredPaths = [Machine("WinSxS")],
            WhenCancelled = CancelledCommand.Finishes,
        };

        Assert.Equal([machine], Apply(Planning(command, machine)).Steps);
    }

    /// <summary>
    /// Windows empties the caller's Recycle Bin and releases the caller's cloud files, and File History's
    /// command cleans the backups of whoever runs it. None of their paths is in the profile, and each is
    /// still the running account's.
    /// </summary>
    [Fact]
    public void AStepThatActsForTheCallingAccountIsWithheldWhereverItsPathIs()
    {
        _environment.ElevatedAsAnotherAccount();
        var elsewhere = Machine("elsewhere");

        CleanupStep[] steps =
        [
            new EmptyRecycleBinStep(Path.Combine(elsewhere, BinName, "S-1-5-21-9"), "Recycle Bin"),
            new ReleaseLocalCopiesStep(elsewhere, "OneDrive", "Free up space"),
            new RunCommandStep("FhManagew.exe", "-cleanup 365 -quiet", "Drop old File History versions")
            {
                MeasuredPaths = [elsewhere],
                ReadsCallingAccountSettings = true,
                WhenCancelled = CancelledCommand.Finishes,
            },
        ];

        Assert.Empty(Apply(Planning(steps)).Steps);
    }

    /// <summary>
    /// The account's folder in a volume's Recycle Bin is its own wherever the volume is mounted, and is
    /// recognised by the account's identifier under the bin's name rather than by the drive's root.
    /// </summary>
    [Fact]
    public void TheRunningAccountsFolderInARecycleBinIsWithheld()
    {
        _environment.ElevatedAsAnotherAccount();
        var volume = Machine("volume");
        var own = Holding(volume, BinName, FakeUserEnvironment.SecurityIdentifier);
        var shared = new DeleteDirectoryStep(Machine("shared"), "Shared");

        var applied = Apply(Planning(new DeleteDirectoryStep(own, "Recycle Bin"), shared));

        Assert.Equal([shared], applied.Steps);
        Assert.Contains(applied.ProtectedPaths, p => p.Path == own && p.HeldContentBefore);
    }

    /// <summary>
    /// A known folder Windows has moved off the profile, as OneDrive's backup or a redirection does, still
    /// belongs to the account, so a step inside it is withheld.
    /// </summary>
    [Fact]
    public void AFolderMovedOffTheProfileIsTheRunningAccountsToo()
    {
        var moved = Machine("Redirected-Pictures");
        _environment.WithPersonalFolderAt(moved).ElevatedAsAnotherAccount();

        var inside = new DeleteDirectoryStep(Holding(moved, "thumbnails"), "Thumbnails");

        Assert.Empty(Apply(Planning(inside)).Steps);
    }

    /// <summary>
    /// Where Windows would not say who is signed in, nobody can say whose these folders are, so they are
    /// withheld as another account's are, and the note says why.
    /// </summary>
    [Fact]
    public void AnAccountNobodyCanConfirmIsWithheldToo()
    {
        _environment.WithNoSessionUser();
        var step = new DeleteDirectoryStep(Holding(_environment.LocalAppData, "Tool", "Cache"), "Tool cache");

        var applied = Apply(Planning(step));

        Assert.Empty(applied.Steps);
        var note = Assert.Single(applied.Notes);
        Assert.Contains("One item here belongs", note.Message, StringComparison.Ordinal);
        Assert.Contains("Windows would not say", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The §5.6 negative for the row where the wrong account is another person's deleted files: planned
    /// under the mismatch, the run empties no bin, and proves both the running account's bin and the
    /// signed-in user's bin standing, each with what it held.
    /// </summary>
    [Fact]
    public async Task ARunPlannedAsAnotherAccountLeavesBothAccountsBinsStandingAndProvesIt()
    {
        _environment.ElevatedAsAnotherAccount();
        var (provider, emptier, running, signedIn) = BinsOnOneVolume();

        Assert.NotEmpty((await provider.PlanAsync()).Steps);

        var planner = new CleanupPlanner([provider], volumes: _volumes, account: RunningAccount.Of(_environment));
        var row = Assert.Single(await planner.PlanAsync([provider]));

        Assert.Empty(row.Plan!.Steps);
        Assert.Equal(FindingStatus.AnotherAccountKept, row.ToStatus(isElevated: true));
        Assert.Contains(
            row.Plan.ProtectedPaths,
            p => p.Path == running && p.Withheld == Withholding.AnotherAccount && p.HeldContentBefore);

        var result = Assert.Single(await planner.ExecuteAsync([row]));

        Assert.Empty(emptier.VolumeRoots);
        Assert.NotEmpty(Directory.EnumerateFiles(running));
        Assert.NotEmpty(Directory.EnumerateFiles(signedIn));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        foreach (var bin in new[] { running, signedIn })
        {
            Assert.Contains(
                result.Verification.Checks,
                check => string.Equals(check.Subject, bin, StringComparison.OrdinalIgnoreCase)
                    && check.Outcome == VerificationOutcome.Survived);
        }
    }

    /// <summary>
    /// A row left out of a run still owes the proof of what it withheld, because an over-broad rule
    /// elsewhere in that run is exactly what could take another account's files.
    /// </summary>
    [Fact]
    public async Task ARowThatDoesNotRunOwesTheCheckOfWhatItWithheld()
    {
        _environment.ElevatedAsAnotherAccount();
        var (provider, _, running, _) = BinsOnOneVolume();

        var planner = new CleanupPlanner([provider], volumes: _volumes, account: RunningAccount.Of(_environment));
        var row = Assert.Single(await planner.PlanAsync([provider]));

        var owed = Assert.Single(StandingProof.For([row], running: []));

        Assert.True(owed.Plan!.HasSomethingToProve);
        Assert.Contains(owed.Plan.ProtectedPaths, p => p.Path == running && p.Withheld == Withholding.AnotherAccount);
        Assert.All(owed.Plan.ProtectedPaths, p => Assert.NotEqual(Withholding.None, p.Withheld));
    }

    /// <summary>
    /// The protection is held to the bin's content, because a call that empties a bin leaves its folder:
    /// a running account's bin emptied after the plan was made fails verification by name.
    /// </summary>
    [Fact]
    public async Task ARunningAccountsBinEmptiedAnywayFailsVerification()
    {
        _environment.ElevatedAsAnotherAccount();
        var (provider, _, running, _) = BinsOnOneVolume();

        var planner = new CleanupPlanner([provider], volumes: _volumes, account: RunningAccount.Of(_environment));
        var plan = Assert.Single(await planner.PlanAsync([provider])).Plan!;

        foreach (var file in Directory.EnumerateFiles(running))
        {
            File.Delete(file);
        }

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(
            verification.Failures,
            failure => string.Equals(failure.Subject, running, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One volume holding the running account's bin and the signed-in user's, each with a deleted file in
    /// it, and the Recycle Bin row over it with an emptier that records every call.
    /// </summary>
    private (RecycleBinProvider Provider, FakeRecycleBinEmptier Emptier, string Running, string SignedIn) BinsOnOneVolume()
    {
        var volume = _temp.CreateDirectory("volumes", "C");
        _volumes.With(volume, DriveType.Fixed, VolumeReadiness.Ready, VolumeFeatures.ReparsePoints);

        var running = Holding(volume, BinName, FakeUserEnvironment.SecurityIdentifier);
        var signedIn = Holding(volume, BinName, FakeUserEnvironment.SignedInSecurityIdentifier);
        var emptier = new FakeRecycleBinEmptier();

        var provider = new RecycleBinProvider(
            emptier,
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            volumes: _volumes);

        return (provider, emptier, running, signedIn);
    }
}
