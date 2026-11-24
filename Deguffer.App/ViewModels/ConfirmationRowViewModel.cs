using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row of the clean's confirmation as the user answers it: whether it is still ticked, whether its
/// acknowledgement is ticked, and what is typed in its box. What §7 asks of the row, and whether the
/// answer suffices, are the <see cref="CleanConfirmationItem"/>'s.
/// </summary>
public sealed partial class ConfirmationRowViewModel(CleanConfirmationItem item) : ObservableObject
{
    public CleanConfirmationItem Item { get; } = item;

    /// <summary>
    /// Whether the row is still in the clean. Ticked when the dialog opens, because the user ticked it on
    /// the page; unticking it here is how a row is left out without stopping the clean.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsksAcknowledgement), nameof(AsksTyping), nameof(StatesConsequence))]
    public partial bool IsIncluded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAcknowledged { get; set; }

    [ObservableProperty]
    public partial string TypedPhrase { get; set; } = string.Empty;

    /// <summary>
    /// Whether the row's consequence is shown. Only while the row is going: a row left out costs nothing,
    /// and its sentence would read as a claim about the clean.
    /// </summary>
    public bool StatesConsequence => IsIncluded && Item.Consequence is not null;

    /// <summary>Whether the acknowledgement is offered, which a row left out does not need.</summary>
    public bool AsksAcknowledgement => IsIncluded && Item.Acknowledgement is not null;

    /// <summary>Whether the typed box is offered, which a row left out does not need.</summary>
    public bool AsksTyping => IsIncluded && Item.Typing is not null;

    public string Consequence => Item.Consequence ?? string.Empty;

    public string Acknowledgement => Item.Acknowledgement ?? string.Empty;

    public string Instruction => Item.Typing?.Instruction ?? string.Empty;

    public string Phrase => Item.Typing?.Phrase ?? string.Empty;

    public string BoxName => Item.Typing?.BoxName ?? string.Empty;

    /// <summary>
    /// The UI Automation ids of the row's controls, from the provider's stable id, so a script reaches
    /// one location whatever its wording or its place in the dialog.
    /// </summary>
    public string IncludeAutomationId => $"ConfirmInclude.{Item.ProviderId}";

    public string AcknowledgementAutomationId => $"ConfirmAcknowledge.{Item.ProviderId}";

    public string PhraseAutomationId => $"ConfirmPhrase.{Item.ProviderId}";

    public CleanConfirmationReply Reply => new(Item.ProviderId, IsIncluded, IsAcknowledged, TypedPhrase);
}
