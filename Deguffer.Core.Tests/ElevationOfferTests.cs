using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether the preview offers to relaunch as administrator.
///
/// The decision lives in Core rather than the view-model so it is provable without a WinUI host —
/// the same reason §5.5's route choice sits behind <c>IMftSource</c>. Getting it wrong is not a
/// data-loss bug, but offering elevation for a problem it cannot fix teaches the user that the
/// prompt is noise, and then they dismiss the one that mattered.
/// </summary>
public sealed class ElevationOfferTests
{
    [Fact]
    public void OffersWhenAnUnelevatedScanFellBackForWantOfRights()
    {
        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false,
            [FindingWith(FallbackReason.NotElevated)]));
    }

    /// <summary>
    /// Before any scan there is nothing to read, so the one fact available decides it. Withholding
    /// the offer until a scan has run puts the elevated scan behind the unelevated one it exists to
    /// replace, which is the whole of the complaint in issue #12.
    /// </summary>
    [Fact]
    public void OffersBeforeAnythingHasBeenScanned()
    {
        Assert.True(ElevationOffer.ShouldOffer(isElevated: false));
    }

    /// <summary>Already elevated, so there is nothing to relaunch for whatever a scan later finds.</summary>
    [Fact]
    public void DoesNotOfferBeforeAnythingHasBeenScannedWhenAlreadyElevated()
    {
        Assert.False(ElevationOffer.ShouldOffer(isElevated: true));
    }

    /// <summary>Explore's own scan, which has one route and so one reason rather than a plan each.</summary>
    [Fact]
    public void OffersWhenAnExploreScanWalkedForWantOfRights()
    {
        Assert.True(ElevationOffer.ShouldOffer(isElevated: false, FallbackReason.NotElevated));
    }

    /// <summary>
    /// The dangerous direction again, on the page that draws a whole volume: none of these is fixed
    /// by rights, and <see cref="FallbackReason.None"/> means the table already answered.
    /// </summary>
    [Theory]
    [InlineData(FallbackReason.None)]
    [InlineData(FallbackReason.NotNtfsVolume)]
    [InlineData(FallbackReason.VolumeNotAddressable)]
    [InlineData(FallbackReason.MasterFileTableIncomplete)]
    public void DoesNotOfferForAnExploreFallbackElevationCannotFix(FallbackReason reason)
    {
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, reason));
    }

    /// <summary>The relaunched instance must not be offered the relaunch it already performed.</summary>
    [Fact]
    public void DoesNotOfferForAnExploreScanWhenAlreadyElevated()
    {
        Assert.False(ElevationOffer.ShouldOffer(isElevated: true, FallbackReason.NotElevated));
    }

    /// <summary>
    /// The wording follows the same state the offer does. A button offering to redo a scan the user
    /// has not run yet is what made the elevated scan read as a second step.
    /// </summary>
    [Fact]
    public void SaysScanBeforeAScanHasRunAndRescanAfterwards()
    {
        Assert.Equal("Elevate and scan", ElevationOffer.Label(hasScanned: false));
        Assert.Equal("Elevate and rescan", ElevationOffer.Label(hasScanned: true));
    }

    /// <summary>
    /// The fast path already ran, so there is nothing to offer. Also the state the relaunched
    /// instance lands in: the button must not survive the restart that satisfied it.
    /// </summary>
    [Fact]
    public void DoesNotOfferWhenTheFastPathServedTheScan()
    {
        Assert.False(ElevationOffer.ShouldOffer(
            isElevated: true,
            [FindingWith(FallbackReason.None)]));
    }

    /// <summary>
    /// The dangerous direction. A non-NTFS volume has no file table whoever is asking, so elevating
    /// changes nothing — offering it promises a speed-up that cannot arrive.
    /// </summary>
    [Theory]
    [InlineData(FallbackReason.NotNtfsVolume)]
    [InlineData(FallbackReason.VolumeNotAddressable)]
    [InlineData(FallbackReason.MasterFileTableIncomplete)]
    public void DoesNotOfferForAFallbackElevationCannotFix(FallbackReason reason)
    {
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, [FindingWith(reason)]));
    }

    /// <summary>
    /// Already elevated and still refused: the rights are not the problem, so a relaunch would
    /// produce an identical slow scan and a second UAC prompt for nothing.
    /// </summary>
    [Fact]
    public void DoesNotOfferWhenAlreadyElevated()
    {
        Assert.False(ElevationOffer.ShouldOffer(
            isElevated: true,
            [FindingWith(FallbackReason.NotElevated)]));
    }

    /// <summary>
    /// Providers measure different volumes, so one slow plan among fast ones is normal — and it is
    /// the one worth acting on.
    /// </summary>
    [Fact]
    public void OffersWhenOnlySomeProvidersTookTheSlowRoute()
    {
        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false,
            [
                FindingWith(FallbackReason.None),
                FindingWith(FallbackReason.NotNtfsVolume),
                FindingWith(FallbackReason.NotElevated),
            ]));
    }

    /// <summary>An absent toolchain yields a finding with no plan, which must not be dereferenced.</summary>
    [Fact]
    public void DoesNotOfferForAnAbsentToolchain()
    {
        Assert.False(ElevationOffer.ShouldOffer(
            isElevated: false,
            [new Finding(new StubProvider(), IsPresent: false, Plan: null)]));
    }

    /// <summary>
    /// The second claim, and it is independent of the first. A plan whose sizes came straight off
    /// the file table can still hold a step under <c>C:\Windows</c> that nobody unelevated may
    /// perform, and the fallback reason says nothing about that — so reading only the fallback would
    /// leave the user looking at a row with no way to act on it and no button to fix that.
    /// </summary>
    [Fact]
    public void OffersWhenAPlanHoldsAStepOnlyAnAdministratorMayCarryOut()
    {
        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false,
            [FindingWith(FallbackReason.None, requiresElevation: true)]));
    }

    /// <summary>
    /// A non-NTFS volume takes the walk whoever asks, so the fallback alone would refuse — but a
    /// step under the Windows directory on that volume still needs the rights. The two conditions
    /// are read separately rather than one gating the other.
    /// </summary>
    [Fact]
    public void OffersForAnUnperformableStepEvenWhereTheFallbackCannotBeFixed()
    {
        Assert.True(ElevationOffer.ShouldOffer(
            isElevated: false,
            [FindingWith(FallbackReason.NotNtfsVolume, requiresElevation: true)]));
    }

    /// <summary>Elevated already, so the step can run and a relaunch would buy a second UAC prompt.</summary>
    [Fact]
    public void DoesNotOfferForSuchAStepWhenAlreadyElevated()
    {
        Assert.False(ElevationOffer.ShouldOffer(
            isElevated: true,
            [FindingWith(FallbackReason.None, requiresElevation: true)]));
    }

    private static Finding FindingWith(FallbackReason reason, bool requiresElevation = false) =>
        new(new StubProvider(), IsPresent: true, new CleanupPlan
        {
            ProviderId = "stub",
            ProviderName = "Stub cache",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Fallback = reason,
            Steps = requiresElevation
                ? [new DeleteDirectoryStep(@"C:\Windows\Minidump", "Stop error dumps.") { RequiresElevation = true }]
                : [],
        });

    private sealed class StubProvider : ICleanupProvider
    {
        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => [];

        public string Id => "stub";

        public string Name => "Stub cache";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderDescription Description { get; } = new()
        {
            Application = "A stub, standing in for a real toolchain.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public void InvalidateCaches() { }

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        // These tests hand the offer a plan directly; nothing here is ever planned, run or verified.
        public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
