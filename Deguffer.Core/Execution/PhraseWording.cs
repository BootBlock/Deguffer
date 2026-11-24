namespace Deguffer.Core.Execution;

/// <summary>What a dialog says around the box a <see cref="ConfirmationLevel.TypedPhrase"/> is typed into.</summary>
/// <param name="Phrase">The words to type, from <see cref="ConfirmationRequirement.RequiredPhrase"/>.</param>
public sealed record PhraseWording(string Phrase)
{
    /// <summary>The line above the box, quoting the phrase.</summary>
    public string Instruction => $"Type “{Phrase}” to confirm.";

    /// <summary>
    /// The box's accessible name. The box shows the phrase only as a placeholder, which a screen reader
    /// stops reading once the user types, so the name says what to type.
    /// </summary>
    public string BoxName => $"Type {Phrase} to confirm deletion";
}
