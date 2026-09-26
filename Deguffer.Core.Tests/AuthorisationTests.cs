using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which findings of a selection §7 lets a clean take, and what it asked to get there.
/// </summary>
public sealed class AuthorisationTests
{
    private static Finding Selected(string id, SafetyTier tier, bool empty = false) =>
        new(new FakeCleanupProvider(id, tier), IsPresent: true, new CleanupPlan
        {
            ProviderId = id,
            ProviderName = id,
            Tier = tier,
            WhatHappensOnNextUse = "Gone.",
            Steps = empty
                ? []
                : [new DeleteDirectoryStep($@"C:\Users\testuser\AppData\Local\{id}", "Files") { Estimated = ScanSize.FromLengths(10) }],
        });

    /// <summary>Answers every question put to it, and records each one.</summary>
    private sealed class Asker(bool agree)
    {
        public List<ConfirmationRequirement> Asked { get; } = [];

        public Task<Confirmation?> AskAsync(ConfirmationRequirement requirement, CancellationToken ct)
        {
            Asked.Add(requirement);

            return Task.FromResult(agree ? new Confirmation(requirement.ProviderId, requirement.RequiredPhrase) : null);
        }
    }

    [Fact]
    public async Task ARegenerableCacheIsAuthorisedWithoutAQuestion()
    {
        var asker = new Asker(agree: false);

        var result = await Authorisation.CollectAsync(
            [Selected("npm", SafetyTier.RegenerableCache)], requireTypedPhrase: true, asker.AskAsync);

        Assert.Equal(["npm"], result.Authorised.Select(f => f.Provider.Id));
        Assert.Empty(result.Confirmations);
        Assert.Empty(asker.Asked);
        Assert.Equal(0, result.Declined);
    }

    [Fact]
    public async Task AnAnsweredQuestionAuthorisesItsFindingAndCarriesTheAnswer()
    {
        var asker = new Asker(agree: true);

        var result = await Authorisation.CollectAsync(
            [Selected("sdk", SafetyTier.RegenerableWithCost), Selected("logs", SafetyTier.UserData)],
            requireTypedPhrase: true,
            asker.AskAsync);

        Assert.Equal(["sdk", "logs"], result.Authorised.Select(f => f.Provider.Id));
        Assert.Equal(["sdk", "logs"], result.Confirmations.Select(c => c.ProviderId));
        Assert.Equal(
            [ConfirmationLevel.Acknowledgement, ConfirmationLevel.TypedPhrase],
            asker.Asked.Select(r => r.Level));
    }

    /// <summary>
    /// Declining is a decision about one finding: it is dropped, and the rest of the selection still
    /// runs, the way a dismissed UAC prompt leaves the app running unelevated.
    /// </summary>
    [Fact]
    public async Task ADeclinedFindingIsDroppedAndTheRestGoAhead()
    {
        var asker = new Asker(agree: false);

        var result = await Authorisation.CollectAsync(
            [Selected("sdk", SafetyTier.RegenerableWithCost), Selected("npm", SafetyTier.RegenerableCache)],
            requireTypedPhrase: true,
            asker.AskAsync);

        Assert.Equal(["npm"], result.Authorised.Select(f => f.Provider.Id));
        Assert.Empty(result.Confirmations);
        Assert.Equal(1, result.Declined);
    }

    /// <summary>
    /// A plan that destroys nothing is neither asked about nor run, and does not count as declined:
    /// nothing was put to the user, so "no selected item was confirmed" would describe the wrong event.
    /// </summary>
    [Fact]
    public async Task AFindingWithNothingToRemoveIsNeitherAskedAboutNorRun()
    {
        var asker = new Asker(agree: true);

        var result = await Authorisation.CollectAsync(
            [
                Selected("sdk", SafetyTier.RegenerableWithCost, empty: true),
                new Finding(new FakeCleanupProvider("gone"), IsPresent: false, Plan: null),
            ],
            requireTypedPhrase: true,
            asker.AskAsync);

        Assert.Empty(result.Authorised);
        Assert.Empty(asker.Asked);
        Assert.Equal(0, result.Declined);
    }

    /// <summary>
    /// With the typed phrase switched off, Tier 3 asks nothing of its own: the shell's blanket
    /// confirmation covers it instead. See <see cref="ConfirmationRequirement.NotPromptedFor{T}"/>.
    /// </summary>
    [Fact]
    public async Task UserDataIsNotAskedAboutAgainWhenTheTypedPhraseIsOff()
    {
        var asker = new Asker(agree: false);

        var result = await Authorisation.CollectAsync(
            [Selected("logs", SafetyTier.UserData)], requireTypedPhrase: false, asker.AskAsync);

        Assert.Equal(["logs"], result.Authorised.Select(f => f.Provider.Id));
        Assert.Empty(asker.Asked);
    }

    /// <summary>
    /// No answer authorises Tier 4, so it is refused without a question, whatever the asker would have
    /// said. It is counted as declined: something was selected and did not go.
    /// </summary>
    [Fact]
    public async Task DoNotTouchIsRefusedWithoutAQuestion()
    {
        var asker = new Asker(agree: true);

        var result = await Authorisation.CollectAsync(
            [Selected("config", SafetyTier.DoNotTouch)], requireTypedPhrase: true, asker.AskAsync);

        Assert.Empty(result.Authorised);
        Assert.Empty(result.Confirmations);
        Assert.Empty(asker.Asked);
        Assert.Equal(1, result.Declined);
    }

    [Fact]
    public async Task ACancelledRunAsksNothingFurther()
    {
        var asker = new Asker(agree: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Authorisation.CollectAsync(
            [Selected("sdk", SafetyTier.RegenerableWithCost)], requireTypedPhrase: true, asker.AskAsync, cancelled.Token));

        Assert.Empty(asker.Asked);
    }
}
