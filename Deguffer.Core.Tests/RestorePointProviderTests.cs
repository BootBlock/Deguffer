using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.SystemProtection;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every restore point but the newest, removed by System Restore itself at Tier 3, and proved afterwards
/// by what Windows still lists rather than by any path.
///
/// <para>System Protection is <see cref="FakeSystemProtection"/> throughout. The real one lists only for an
/// administrator, and a test that reached it could remove the restore points of whoever ran the suite.</para>
/// </summary>
public sealed class RestorePointProviderTests : IDisposable
{
    private const long Gigabyte = 1_000_000_000;

    /// <summary><c>VSS_CTX_CLIENT_ACCESSIBLE_WRITERS</c>: persistent, client-accessible, not auto-released.</summary>
    private const int RestorePointKind = 0x1 | 0x4 | 0x8;

    /// <summary><c>VSS_CTX_APP_ROLLBACK</c>: what a backup product makes.</summary>
    private const int BackupKind = 0x1 | 0x8;

    private static readonly Guid OtherProvider = new("11111111-2222-3333-4444-555555555555");

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemProtection _protection = new()
    {
        Storage = new ShadowStorage(Statement.Stated, UsedBytes: 12 * Gigabyte, AllocatedBytes: 13 * Gigabyte, MaximumBytes: 50 * Gigabyte),
        FreedPerRemoval = 3 * Gigabyte,
    };

    public RestorePointProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private RestorePointProvider Provider() =>
        new(_protection, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Three restore points, listed out of order so the newest is decided by number, not by place.</summary>
    private (RestorePoint Oldest, RestorePoint Middle, RestorePoint Newest) ThreePoints()
    {
        var middle = _protection.Add(41, daysAgo: 20, "Installed Example Tool");
        var newest = _protection.Add(42, daysAgo: 2);
        var oldest = _protection.Add(40, daysAgo: 30);
        _protection.AddCopy(ShadowCopy.SystemProvider, RestorePointKind);
        _protection.AddCopy(ShadowCopy.SystemProvider, RestorePointKind);
        _protection.AddCopy(ShadowCopy.SystemProvider, RestorePointKind);

        return (oldest, middle, newest);
    }

    private static RemoveRestorePointsStep Step(CleanupPlan plan) => Assert.IsType<RemoveRestorePointsStep>(Assert.Single(plan.Steps));

    [Fact]
    public async Task ItPlansSystemRestoresOwnRemovalOfEveryRestorePointButTheNewest()
    {
        var (oldest, middle, newest) = ThreePoints();

        var plan = await Provider().PlanAsync();
        var step = Step(plan);

        Assert.Equal([oldest.SequenceNumber, middle.SequenceNumber], step.Removes.Select(p => p.SequenceNumber));
        Assert.Equal(newest, step.Kept);
        Assert.True(step.RequiresElevation);
        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(step.Subjects);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith($"Kept: the restore point {newest.Label}, the newest.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row names no cleaned place, which holds only while its plan deletes no file and sends no
    /// command anywhere a file could be.
    /// </summary>
    [Fact]
    public async Task ThePlanCleansNoPathSoItNamesNoCleanedPlace()
    {
        ThreePoints();

        var plan = await Provider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.Empty(CleanedPlaceCoverage.Cleaned(plan));
    }

    /// <summary>
    /// Windows gives no figure for one restore point, so the row offers all the storage Windows states as
    /// the most the removal could free, and says that it includes what stays.
    /// </summary>
    [Fact]
    public async Task TheFigureIsWindowsStorageOfferedAsACeiling()
    {
        ThreePoints();

        var plan = await Provider().PlanAsync();

        Assert.Equal(12 * Gigabyte, plan.EstimatedBytes);
        Assert.True(plan.Estimated.IsCeiling);
        Assert.Contains(plan.Notes, n => n.Message.Contains("includes the restore point that stays", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItIsTier3NeverPreSelectedAndConfirmedWithItsName()
    {
        ThreePoints();

        var provider = Provider();
        var requirement = ConfirmationRequirement.For(await provider.PlanAsync());

        Assert.Equal(SafetyTier.UserData, provider.Tier);
        Assert.False(provider.Tier.IsPreSelectedByDefault());
        Assert.Equal(ConfirmationLevel.TypedPhrase, requirement.Level);
        Assert.Contains("can no longer be returned to any of these restore points", requirement.Consequence, StringComparison.Ordinal);
    }

    /// <summary>The newest is never offered, so a machine with one restore point has nothing here.</summary>
    [Fact]
    public async Task OneRestorePointOffersNothingAndTheRowIsAbsent()
    {
        _protection.Add(7, daysAgo: 1);

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.False(await provider.IsPresentAsync());
        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task AnUnelevatedListingIsNotExaminedAndIsToldToElevate()
    {
        _protection.PointsAnswer = ListingAnswer.NeedsElevation;

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Scanning as administrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AListingWindowsFailedOffersNothingAndIsNotClear()
    {
        _protection.PointsAnswer = ListingAnswer.Failed;

        var plan = await Provider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("0x80004005", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6 cannot be kept without the list of shadow copies, so a plan that could not make it offers
    /// nothing rather than a removal it could not check.
    /// </summary>
    [Theory]
    [InlineData(ListingAnswer.NeedsElevation)]
    [InlineData(ListingAnswer.Failed)]
    public async Task ShadowCopiesWindowsWouldNotListOfferNothing(ListingAnswer answer)
    {
        ThreePoints();
        _protection.CopiesAnswer = answer;

        var plan = await Provider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check afterwards", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every restore point is kept as a shadow copy, so a listing that holds none that could be one while
    /// System Restore lists two or more cannot be seeing them, and the shadow copies it says are not there
    /// are ones the clean could not prove standing.
    /// </summary>
    [Fact]
    public async Task AShadowCopyListingBlindToTheRestorePointsOffersNothing()
    {
        _protection.Add(40, daysAgo: 30);
        _protection.Add(41, daysAgo: 2);
        _protection.AddCopy(OtherProvider, BackupKind);

        var plan = await Provider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check afterwards", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageWindowsWouldNotStateOffersNothing()
    {
        ThreePoints();
        _protection.Storage = new ShadowStorage(Statement.NotStated);

        var plan = await Provider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>The shadow copies the run must prove standing are exactly those that cannot be a restore point's.</summary>
    [Fact]
    public async Task EveryShadowCopyThatCannotBeARestorePointsIsCarriedToBeProved()
    {
        ThreePoints();
        var backup = _protection.AddCopy(ShadowCopy.SystemProvider, BackupKind);
        var foreign = _protection.AddCopy(OtherProvider, RestorePointKind);

        var step = Step(await Provider().PlanAsync());

        Assert.Equal([backup.Id, foreign.Id], step.OtherCopies.Select(c => c.Id));
    }

    [Fact]
    public async Task TheRunRemovesExactlyThePlannedRestorePointsOldestFirstAndReportsWindowsFigure()
    {
        var (oldest, middle, newest) = ThreePoints();

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal([oldest.SequenceNumber, middle.SequenceNumber], _protection.Removed);
        Assert.Equal([newest], _protection.Points);
        Assert.Equal(6 * Gigabyte, result.BytesReclaimed);
        Assert.True(Assert.Single(result.Steps).Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A restore point made after the preview is not one the user saw, so it stays.</summary>
    [Fact]
    public async Task ARestorePointMadeAfterThePreviewIsNotRemoved()
    {
        var (oldest, middle, _) = ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.Add(43, daysAgo: 0, "Installed Another Tool");

        await provider.ExecuteAsync(plan);

        Assert.Equal([oldest.SequenceNumber, middle.SequenceNumber], _protection.Removed);
        Assert.Contains(_protection.Points, p => p.SequenceNumber == 43);
    }

    /// <summary>
    /// The run keeps whatever is newest when it reaches the step. If the one the preview kept went in the
    /// meantime, the newest of the rest stays instead, so the machine is never left with none.
    /// </summary>
    [Fact]
    public async Task IfTheKeptRestorePointWentSinceThePreviewTheNewestOfTheRestStays()
    {
        var (oldest, middle, newest) = ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.Points.Remove(newest);

        await provider.ExecuteAsync(plan);

        Assert.Equal([oldest.SequenceNumber], _protection.Removed);
        Assert.Equal([middle], _protection.Points);
    }

    [Fact]
    public async Task ARestorePointGoneBeforeTheRunIsNotAskedAbout()
    {
        var (oldest, middle, _) = ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.Points.Remove(oldest);

        await provider.ExecuteAsync(plan);

        Assert.Equal([middle.SequenceNumber], _protection.Removed);
    }

    /// <summary>
    /// A restore point is matched whole, never by its number alone, so one that took a planned number after
    /// System Protection was switched off and on is not the one the user saw, and stays.
    /// </summary>
    [Fact]
    public async Task ARestorePointThatOnlySharesAPlannedNumberIsNotRemoved()
    {
        var (oldest, middle, _) = ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.Points.Remove(oldest);
        _protection.Points.Add(oldest with { Created = oldest.Created.AddDays(10), Description = "Installed Another Tool" });

        await provider.ExecuteAsync(plan);

        Assert.Equal([middle.SequenceNumber], _protection.Removed);
    }

    /// <summary>A clean cancelled between restore points stops there, and says it was interrupted.</summary>
    [Fact]
    public async Task ACleanCancelledBetweenRestorePointsStopsAndIsInterrupted()
    {
        var (oldest, _, _) = ThreePoints();
        using var cancel = new CancellationTokenSource();
        _protection.OnRemoved = _ => cancel.Cancel();

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync(), ct: cancel.Token);

        Assert.Equal([oldest.SequenceNumber], _protection.Removed);
        Assert.True(Assert.Single(result.Steps).Interrupted);
        Assert.True(result.Interrupted);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task ARunThatCannotListRemovesNothing()
    {
        ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.PointsAnswer = ListingAnswer.NeedsElevation;

        var result = await provider.ExecuteAsync(plan);
        var outcome = Assert.Single(result.Steps);

        Assert.Empty(_protection.Removed);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Nothing was removed:", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>A refusal part way says which restore point stayed and why, and stops asking.</summary>
    [Fact]
    public async Task ARemovalSystemRestoreRefusesIsReportedAndStopsTheRest()
    {
        var (oldest, _, _) = ThreePoints();
        _protection.Removals[oldest.SequenceNumber] = RemovalAnswer.NeedsElevation;

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());
        var outcome = Assert.Single(result.Steps);

        Assert.Equal([oldest.SequenceNumber], _protection.Removed);
        Assert.False(outcome.Succeeded);
        Assert.Contains($"the restore point {oldest.Label}: System Restore removes restore points only for an administrator", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A volume Windows gives no figure for, such as one VSS does not support, leaves the reclaim to the
    /// volumes it answered for both times. Observed: an elevated read on Windows 11 stated seven volumes and
    /// not an eighth, and requiring every volume would count nothing on that machine, ever.
    /// </summary>
    [Fact]
    public async Task AVolumeWindowsNeverStatesDoesNotStopTheReclaimBeingCounted()
    {
        ThreePoints();
        _protection.OtherVolumes.Add(new VolumeShadowStorage(@"E:", new ShadowStorage(Statement.NotStated)));

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.Equal(6 * Gigabyte, result.BytesReclaimed);
    }

    [Fact]
    public async Task StorageWindowsWouldNotStateAfterwardsCountsNothing()
    {
        ThreePoints();

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.OnRemoved = _ => _protection.Storage = new ShadowStorage(Statement.NotStated);

        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(0, result.BytesReclaimed);
        Assert.Contains("nothing is counted as freed", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    /// <summary>§5.6 is live: a removal that took another program's shadow copy fails the run.</summary>
    [Fact]
    public async Task ARemovalThatTookAnotherProgramsShadowCopyFailsTheNegative()
    {
        ThreePoints();
        var backup = _protection.AddCopy(OtherProvider, BackupKind);
        _protection.OnRemoved = _ => _protection.Copies.Remove(backup);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        var failure = Assert.Single(result.Verification.Failures);
        Assert.Equal(backup.Named, failure.Subject);
        Assert.StartsWith("MISSING", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovalThatTookTheKeptRestorePointFailsTheNegative()
    {
        var (_, _, newest) = ThreePoints();
        _protection.OnRemoved = _ => _protection.Points.Remove(newest);

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        Assert.Equal($"The restore point {newest.Label}", Assert.Single(result.Verification.Failures).Subject);
    }

    [Fact]
    public async Task ARemovalThatChangedSystemProtectionsLimitFailsTheNegative()
    {
        ThreePoints();
        _protection.OnRemoved = _ => _protection.Storage = _protection.Storage with { MaximumBytes = 10 * Gigabyte };

        var provider = Provider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.False(result.Verification!.Passed);
        var failure = Assert.Single(result.Verification.Failures);
        Assert.Equal(@"System Protection's limit on C:\", failure.Subject);
        Assert.StartsWith("CHANGED", failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>A listing Windows refuses afterwards proves nothing either way, and says so.</summary>
    [Fact]
    public async Task SurvivorsWindowsWouldNotListAfterwardsAreUnverified()
    {
        ThreePoints();
        _protection.AddCopy(OtherProvider, BackupKind);

        var provider = Provider();
        var plan = await provider.PlanAsync();
        _protection.OnRemoved = _ =>
        {
            _protection.PointsAnswer = ListingAnswer.Failed;
            _protection.CopiesAnswer = ListingAnswer.Failed;
        };

        var result = await provider.ExecuteAsync(plan);

        Assert.False(result.Verification!.Passed);
        Assert.Equal(2, result.Verification.Unverified.Count);
        Assert.Empty(result.Verification.Failures);
    }

    /// <summary>The executor never falls back to the machine's System Restore: a plan holding the step needs the route given.</summary>
    [Fact]
    public async Task AnExecutorGivenNoRouteRefusesThePlanBeforeAnythingRuns()
    {
        ThreePoints();
        var plan = await Provider().PlanAsync();
        var executor = new PlanExecutor(new FakeProcessRunner(), Scanning.ParallelEnumerationScanner.Default, RefusalRecord.For(_environment));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan, null, null, null, CancellationToken.None));
        Assert.Empty(_protection.Removed);
    }

    /// <summary>A declined row runs nothing and has nothing to prove.</summary>
    [Fact]
    public async Task ADeclinedStepRemovesNothing()
    {
        ThreePoints();

        var provider = Provider();
        var result = await provider.ExecuteAsync((await provider.PlanAsync()).NarrowedTo([]));

        Assert.Empty(_protection.Removed);
        Assert.Empty(result.Verification!.Checks);
    }

    /// <summary>Restore points and shadow copies are listed once per planning pass (G4).</summary>
    [Fact]
    public async Task PresenceAndPlanningShareOneListing()
    {
        ThreePoints();

        var provider = Provider();
        await provider.IsPresentAsync();
        await provider.PlanAsync();

        Assert.Equal(1, _protection.Listings);
    }
}
