using Deguffer.Core.Cloud;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The local copies of cloud files, released while every file stays where it is.
///
/// <para>Nothing here is deleted, so the negatives are about what must not change: a pin the user set,
/// an edit the cloud does not have, a folder behind a link, a sync app Deguffer does not recognise. The
/// API refuses none of those, so each one is a rule of Deguffer's and each has a test that names it.</para>
/// </summary>
public sealed class CloudLocalCopiesProviderTests : IDisposable
{
    private const string OneDriveId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Personal";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeCloudFiles _cloud = new();
    private readonly string _root;

    public CloudLocalCopiesProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _root = _temp.CreateDirectory("OneDrive");
        _cloud.Root(OneDriveId, _root, "OneDrive - Personal");
    }

    public void Dispose() => _temp.Dispose();

    private string At(params string[] segments) => Path.Combine([_root, .. segments]);

    private CloudLocalCopiesProvider CreateProvider() => new(
        _environment,
        new FakeProcessRunner(),
        FakeProcessInspector.NothingRunning,
        new FakeDirectoryScanner(),
        _cloud);

    private static ReleaseLocalCopiesStep OnlyStep(CleanupPlan plan) =>
        Assert.IsType<ReleaseLocalCopiesStep>(Assert.Single(plan.Steps));

    [Fact]
    public async Task OffersTheInSyncLocalCopiesAndNamesEachFile()
    {
        _cloud
            .File(At("report.docx"), onDisk: 4_000)
            .Folder(At("Photos"))
            .File(At("Photos", "beach.jpg"), onDisk: 6_000);

        var plan = await CreateProvider().PlanAsync();

        var step = OnlyStep(plan);
        Assert.Equal(_root, step.SyncRoot);
        Assert.Equal("OneDrive", step.SyncApp);
        Assert.Equal([At("Photos", "beach.jpg"), At("report.docx")], step.Files.Select(f => f.Path).Order());
        Assert.Equal(10_000, step.EstimatedBytes);
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    /// <summary>
    /// §5.2 and §5.6 from the other side: this step destroys nothing, so it names no target, and a run
    /// holding it is not a run whose reach nobody can state.
    /// </summary>
    [Fact]
    public async Task TargetsNothingAndLeavesTheRunsReachBounded()
    {
        _cloud.File(At("report.docx"), onDisk: 4_000);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        var reach = RunReach.Of([plan]);
        Assert.Empty(reach.TargetedPaths);
        Assert.False(reach.Unbounded);
    }

    /// <summary>
    /// Every reason a file with something on this PC stays as it is, each beside one that goes, so a
    /// rule that stopped holding would put its file into the step.
    /// </summary>
    [Fact]
    public async Task LeavesEveryFileTheRulesHoldBack()
    {
        var keep = MinimumAge.WithinHours(8, DateTime.UtcNow);

        _cloud
            .File(At("goes.txt"), onDisk: 100)
            .File(At("pinned.txt"), onDisk: 200, pin: PinState.Pinned)
            .File(At("edited.txt"), onDisk: 300, modified: 10)
            .File(At("unsynced.txt"), onDisk: 400, inSync: false)
            .File(At("excluded.txt"), onDisk: 500, pin: PinState.Excluded)
            .File(At("asked.txt"), onDisk: 600, pin: PinState.Unpinned)
            .File(At("online-only.txt"), onDisk: 0)
            .File(At("recent.txt"), onDisk: 700, newest: DateTime.UtcNow.ToFileTimeUtc())
            .Folder(At("Always here"), PinState.Pinned)
            .File(At("Always here", "inherits.txt"), onDisk: 800)
            .Folder(At("Always here", "Deeper"))
            .File(At("Always here", "Deeper", "inherits too.txt"), onDisk: 900);

        var plan = await CreateProvider().PlanAsync(keep);

        var step = OnlyStep(plan);
        Assert.Equal([At("goes.txt")], step.Files.Select(f => f.Path));
        Assert.True(step.WithheldRecent);

        Assert.Contains(plan.Notes, n => n.Message.Contains("always keep on this device", StringComparison.Ordinal)
            && n.Message.Contains("3 file(s)", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains("not uploaded yet", StringComparison.Ordinal)
            && n.Message.Contains("2 file(s)", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains("excluded from sync", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains("already waiting", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pinned folder's pin reaches through a folder that is no placeholder at all, which a sync app
    /// may keep inside a root.
    /// </summary>
    [Fact]
    public async Task APinnedFoldersPinReachesThroughAnOrdinaryFolder()
    {
        _cloud
            .Folder(At("Kept"), PinState.Pinned)
            .PlainFolder(At("Kept", "Plain"))
            .File(At("Kept", "Plain", "inside.txt"), onDisk: 100)
            .PlainFolder(At("Loose"))
            .File(At("Loose", "goes.txt"), onDisk: 100);

        var step = OnlyStep(await CreateProvider().PlanAsync());

        Assert.Equal([At("Loose", "goes.txt")], step.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task NeverEntersALinkOrAFolderWindowsWillNotDescribe()
    {
        _cloud
            .Link(At("Elsewhere"))
            .File(At("Elsewhere", "beyond.txt"), onDisk: 100)
            .Folder(At("Locked"))
            .Refuse(At("Locked"))
            .File(At("Locked", "inside.txt"), onDisk: 100)
            .File(At("goes.txt"), onDisk: 100);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([At("goes.txt")], OnlyStep(plan).Files.Select(f => f.Path));
    }

    [Fact]
    public async Task NeverTouchesAnOrdinaryFileInsideTheRoot()
    {
        _cloud.PlainFile(At("local only.txt")).File(At("goes.txt"), onDisk: 100);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([At("goes.txt")], OnlyStep(plan).Files.Select(f => f.Path));
    }

    /// <summary>§5.2: a sync app Deguffer does not recognise is left alone, named, and protected.</summary>
    [Fact]
    public async Task LeavesAnUnrecognisedSyncAppAloneAndSaysSo()
    {
        var other = _temp.CreateDirectory("Elsewhere Drive");
        _cloud
            .Root("ElsewhereDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Me", other, "Elsewhere Drive")
            .File(Path.Combine(other, "theirs.txt"), onDisk: 100)
            .File(At("goes.txt"), onDisk: 100);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(_root, OnlyStep(plan).SyncRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains("ElsewhereDrive", StringComparison.Ordinal)
            && n.Message.Contains("does not recognise", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == other);
    }

    [Fact]
    public async Task IsNotPresentWhereOnlyAnUnrecognisedSyncAppHasARoot()
    {
        var cloud = new FakeCloudFiles().Root("ElsewhereDrive!S-1-5-21-1!Me", _root, "Elsewhere Drive");

        var provider = new CloudLocalCopiesProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner(), cloud);

        Assert.False(await provider.IsPresentAsync());
        Assert.True(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A request to a sync app that is not running releases nothing, so nothing is offered, and the row
    /// does not read as clear about a folder nobody looked in.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhileTheSyncAppIsNotRunning()
    {
        _cloud.File(At("report.docx"), onDisk: 4_000);
        _cloud.Stop(_root);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("OneDrive is not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARootWhoseOnlyLocalCopiesAreRecentIsNotClear()
    {
        _cloud.File(At("today.txt"), onDisk: 100, newest: DateTime.UtcNow.ToFileTimeUtc());

        var plan = await CreateProvider().PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasRecentContentHeldBack);
    }

    [Fact]
    public async Task ARootWithNothingOnThisPcIsClear()
    {
        _cloud.File(At("online.txt"), onDisk: 0);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.False(plan.HasRecentContentHeldBack);
    }

    [Fact]
    public async Task AsksForReleaseAndDestroysNothing()
    {
        var provider = CreateProvider();
        _cloud
            .File(At("a.txt"), onDisk: 1_000)
            .File(At("b.txt"), onDisk: 2_000)
            .File(At("pinned.txt"), onDisk: 3_000, pin: PinState.Pinned)
            .PlainFile(At("plain.txt"));

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.Equal([At("a.txt"), At("b.txt")], _cloud.Unpinned.Order());
        Assert.Equal(PinState.Pinned, _cloud.PlaceholderAt(At("pinned.txt"))!.Pin);

        var outcome = Assert.Single(result.Steps);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Equal(3_000, outcome.BytesRequested);
        Assert.Equal(3_000, result.BytesRequested);
        Assert.Contains("Asked OneDrive to release", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preview is a promise about which files, and the rules are asked again of each: a file that
    /// gained an edit, or whose folder the user pinned, since the scan stays as it is.
    /// </summary>
    [Fact]
    public async Task JudgesEachFileAgainAtTheClean()
    {
        var provider = CreateProvider();
        _cloud
            .File(At("edited later.txt"), onDisk: 100)
            .Folder(At("Pinned later"))
            .File(At("Pinned later", "inside.txt"), onDisk: 200)
            .File(At("goes.txt"), onDisk: 300);

        var plan = await provider.PlanAsync();
        Assert.Equal(3, OnlyStep(plan).Files.Count);

        _cloud.Change(At("edited later.txt"), p => p with { ModifiedBytes = 5, InSync = false });
        _cloud.Change(At("Pinned later"), p => p with { Pin = PinState.Pinned });

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal([At("goes.txt")], _cloud.Unpinned);
        Assert.Equal(300, result.BytesRequested);
        Assert.Contains("2 file(s) had changed since the scan", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    /// <summary>A file that arrived after the preview is not the preview's to release.</summary>
    [Fact]
    public async Task ActsOnlyOnTheFilesThePlanNamed()
    {
        var provider = CreateProvider();
        _cloud.File(At("planned.txt"), onDisk: 100);

        var plan = await provider.PlanAsync();
        _cloud.File(At("arrived later.txt"), onDisk: 900);

        await provider.ExecuteAsync(plan);

        Assert.Equal([At("planned.txt")], _cloud.Asked);
    }

    [Fact]
    public async Task AsksNothingOfASyncAppThatStoppedSinceTheScan()
    {
        var provider = CreateProvider();
        _cloud.File(At("a.txt"), onDisk: 100);

        var plan = await provider.PlanAsync();
        _cloud.Stop(_root);

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_cloud.Asked);
        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesRequested);
        Assert.Contains("OneDrive is not running", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>§5.6: every file the step named is still there, and still a cloud file.</summary>
    [Fact]
    public async Task VerifiesEveryReleasedFileSurvived()
    {
        var provider = CreateProvider();
        _cloud.File(At("a.txt"), onDisk: 100).File(At("b.txt"), onDisk: 200);

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var checks = result.Verification!.Checks.Where(c => c.Subject != _root).ToList();
        Assert.Equal([At("a.txt"), At("b.txt")], checks.Select(c => c.Subject).Order());
        Assert.All(checks, c => Assert.Equal(VerificationOutcome.Survived, c.Outcome));
        Assert.True(result.Verification.Passed);
    }

    /// <summary>
    /// §5.6's alarm: a release that took the file with it is the over-reach this negative exists for,
    /// and a file gone from a folder still standing is exactly its shape.
    /// </summary>
    [Fact]
    public async Task AReleaseThatTookTheFileFailsVerification()
    {
        var provider = CreateProvider();
        var file = _temp.CreateFile(100, "OneDrive", "a.txt");
        _cloud.File(file, onDisk: 100);

        var plan = await provider.PlanAsync();

        _cloud.BeforeRelease = path =>
        {
            File.Delete(path);
            _cloud.Remove(path);
        };

        var result = await provider.ExecuteAsync(plan);

        var check = Assert.Single(result.Verification!.Checks, c => c.Subject == file);
        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.False(result.Verification.Passed);
    }

    /// <summary>
    /// A file whose whole folder went while the preview sat on screen is not Deguffer's doing, and is
    /// said to be something else's rather than raised as an alarm.
    /// </summary>
    [Fact]
    public async Task AFileWhoseFolderWentFromOutsideIsNotAnAlarm()
    {
        var provider = CreateProvider();
        var folder = At("Gone");
        _cloud.Folder(folder).File(Path.Combine(folder, "a.txt"), onDisk: 100);

        var plan = await provider.PlanAsync();
        _cloud.Remove(folder);

        var result = await provider.ExecuteAsync(plan);

        var check = Assert.Single(result.Verification!.Checks, c => c.Subject == Path.Combine(folder, "a.txt"));
        Assert.Equal(VerificationOutcome.RemovedFromOutside, check.Outcome);
    }

    [Fact]
    public async Task AFileTheSyncAppMadeOrdinaryAgainStillSurvives()
    {
        var provider = CreateProvider();
        _cloud.File(At("a.txt"), onDisk: 100);

        var plan = await provider.PlanAsync();
        _cloud.BeforeRelease = path => _cloud.PlainFile(path);

        var result = await provider.ExecuteAsync(plan);

        var check = Assert.Single(result.Verification!.Checks, c => c.Subject == At("a.txt"));
        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
    }

    /// <summary>
    /// A folder above a planned file turned into a link since the preview: the file the name now leads
    /// to is not the one planned, so it is left alone.
    /// </summary>
    [Fact]
    public async Task LeavesAFileThatNoLongerResolvesWhereThePlanFoundIt()
    {
        var provider = CreateProvider();
        _cloud.File(At("moved.txt"), onDisk: 100).File(At("goes.txt"), onDisk: 200);

        var plan = await provider.PlanAsync();
        _cloud.Redirect(At("moved.txt"), @"C:\Users\testuser\Elsewhere\moved.txt");

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal([At("goes.txt")], _cloud.Unpinned);
        Assert.Equal(200, result.BytesRequested);
    }

    /// <summary>
    /// A root the user reaches through a link of their own resolves somewhere else as a whole, and the
    /// files under it are still released: each is checked against its place under where the root is.
    /// </summary>
    [Fact]
    public async Task ReleasesUnderARootReachedThroughALinkOfItsOwn()
    {
        var provider = CreateProvider();
        _cloud.File(At("goes.txt"), onDisk: 200);
        _cloud.Locate(_root, @"D:\Relocated\OneDrive");

        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal([At("goes.txt")], _cloud.Unpinned);
        Assert.Equal(200, result.BytesRequested);
    }

    /// <summary>A refusal to list the roots is never read as there being none.</summary>
    [Fact]
    public async Task SaysSoWhenWindowsWillNotListTheRoots()
    {
        _cloud.RefusesToListRoots = true;
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains("would not list", StringComparison.Ordinal));
    }

    /// <summary>A folder nobody could read may hold local copies, so the row does not say it is clear.</summary>
    [Fact]
    public async Task ARootWithAFolderWindowsWillNotDescribeIsNotClear()
    {
        _cloud.Folder(At("Locked")).Refuse(At("Locked")).File(At("Locked", "inside.txt"), onDisk: 100);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains("1 item(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every local copy held back by a rule is something the plan declined, not something that is not
    /// there, so the row does not read as clear.
    /// </summary>
    [Fact]
    public async Task ARootWhoseLocalCopiesAreAllHeldBackIsNotClear()
    {
        _cloud.File(At("pinned.txt"), onDisk: 100, pin: PinState.Pinned);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>The question put before a release does not say "Delete" about files nothing deletes.</summary>
    [Fact]
    public async Task IsConfirmedAsAReleaseRatherThanADeletion()
    {
        _cloud.File(At("a.txt"), onDisk: 100);

        var requirement = ConfirmationRequirement.For(await CreateProvider().PlanAsync());

        Assert.Equal(ConfirmationLevel.Acknowledgement, requirement.Level);
        Assert.Equal("Release", requirement.Verb);
    }
}
