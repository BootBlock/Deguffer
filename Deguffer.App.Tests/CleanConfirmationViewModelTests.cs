using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the clean's confirmation dialog wires its ticks and typing to Core's answers: what is enabled,
/// what is announced, and that a tick given in a group is the tick the replies carry. Whether the
/// answers suffice is <see cref="CleanConfirmation"/>'s, tested in Core.
/// </summary>
public sealed class CleanConfirmationViewModelTests
{
    [Fact]
    public void TheButtonIsEnabledOnlyOnceEveryTickedRowIsAnswered()
    {
        var model = new CleanConfirmationViewModel(Confirmation());
        var sdk = Row(model, "sdk");
        var logs = Row(model, "logs");

        Assert.False(model.CanConfirm);

        sdk.IsAcknowledged = true;
        Assert.False(model.CanConfirm);

        logs.TypedPhrase = "logs";
        Assert.True(model.CanConfirm);
    }

    /// <summary>
    /// Unticking a row takes its answer off the screen and its figure out of the button, and the clean
    /// can start without it.
    /// </summary>
    [Fact]
    public void UntickingARowHidesItsQuestionsAndTakesItOutOfTheButton()
    {
        var model = new CleanConfirmationViewModel(Confirmation());
        var sdk = Row(model, "sdk");
        Row(model, "logs").IsIncluded = false;

        Assert.True(sdk.AsksAcknowledgement);
        sdk.IsIncluded = false;

        Assert.False(sdk.AsksAcknowledgement);
        Assert.False(sdk.StatesConsequence);
        Assert.Equal("Clean 1.0 GB", model.ConfirmLabel);
        Assert.Equal("Clean these locations?", model.Title);
        Assert.True(model.CanConfirm);
    }

    /// <summary>
    /// Each answer is announced once for each part of the dialog that depends on it, so the prompt can
    /// bring the title, the button and the total up to date as the user ticks and types.
    /// </summary>
    [Fact]
    public void EveryAnswerAnnouncesTheTitleTheButtonTheTotalAndWhetherItMayBePressed()
    {
        var model = new CleanConfirmationViewModel(Confirmation());
        List<string?> changed = [];
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Row(model, "sdk").IsAcknowledged = true;

        Assert.Equal(
            [
                nameof(CleanConfirmationViewModel.Title),
                nameof(CleanConfirmationViewModel.ConfirmLabel),
                nameof(CleanConfirmationViewModel.TotalLabel),
                nameof(CleanConfirmationViewModel.CanConfirm),
            ],
            changed);
    }

    [Fact]
    public void TheGroupsShowTheRowsTheRepliesRead()
    {
        var model = new CleanConfirmationViewModel(Confirmation());

        model.Groups.Single(g => g.Rows.Any(r => r.Item.ProviderId == "sdk")).Rows.Single().IsIncluded = false;

        Assert.False(Assert.Single(model.Replies, reply => reply.ProviderId == "sdk").Included);
        Assert.Equal(model.Rows.Count, model.Groups.Sum(g => g.Rows.Count));
        Assert.All(model.Groups.SelectMany(g => g.Rows), row => Assert.Contains(row, model.Rows));
    }

    private static ConfirmationRowViewModel Row(CleanConfirmationViewModel model, string id) =>
        model.Rows.Single(row => row.Item.ProviderId == id);

    private static CleanConfirmation Confirmation() => CleanConfirmation.For(
        [Selected("npm", SafetyTier.RegenerableCache), Selected("sdk", SafetyTier.RegenerableWithCost), Selected("logs", SafetyTier.UserData)],
        requireTypedPhrase: true,
        CompletionAction.Nothing);

    private static Finding Selected(string id, SafetyTier tier)
    {
        const long gigabyte = 1024L * 1024 * 1024;

        return new Finding(new FakeCleanupProvider(id, tier), IsPresent: true, new CleanupPlan
        {
            ProviderId = id,
            ProviderName = id,
            Tier = tier,
            WhatHappensOnNextUse = "Gone.",
            Steps = [new DeleteDirectoryStep($@"C:\Users\testuser\AppData\Local\{id}", "Files") { Estimated = new ScanSize(gigabyte, gigabyte) }],
        });
    }
}
