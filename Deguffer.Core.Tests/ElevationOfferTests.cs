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
    [InlineData(FallbackReason.MasterFileTableUnreadable)]
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

    private static Finding FindingWith(FallbackReason reason) =>
        new(new StubProvider(), IsPresent: true, new CleanupPlan
        {
            ProviderId = "stub",
            ProviderName = "Stub cache",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Fallback = reason,
        });

    private sealed class StubProvider : ICleanupProvider
    {
        public string Id => "stub";

        public string Name => "Stub cache";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public string WhatHappensOnNextUse => "Nothing.";

        public void InvalidateCaches() { }

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        // These tests hand the offer a plan directly; nothing here is ever planned, run or verified.
        public Task<CleanupPlan> PlanAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan, IProgress<double>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
