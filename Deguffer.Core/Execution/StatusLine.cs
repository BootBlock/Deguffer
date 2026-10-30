namespace Deguffer.Core.Execution;

/// <summary>
/// What a page's status line says: the sentence, where it came from, and the §5.6 checks it has to
/// answer for, which the page lists beside it.
///
/// <para>The sentence of a removal counts the checks that did not pass and asks the user to look at
/// the folders, and after a removal that touched many of them a count does not say which. The list
/// says which, so it belongs to the sentence and goes with it: a line that replaces this one, a
/// scan's progress or a refusal, carries checks of its own or none, and never names folders it did
/// not mean.</para>
///
/// <para>Where the sentence came from is said rather than worked out from its words, because a page
/// takes back a sentence of one kind and leaves every other alone. See <see cref="StatusOrigin"/>.</para>
/// </summary>
/// <param name="Checks">Every check that did not pass (<see cref="VerificationResult.Unpassed"/>), or none.</param>
public sealed record StatusLine(string Sentence, StatusOrigin Origin, IReadOnlyList<VerificationCheck> Checks)
{
    /// <summary>A line with nothing on it.</summary>
    public static readonly StatusLine Empty = Said(string.Empty);

    /// <summary>A sentence the page says of its own work, which answers for no check.</summary>
    public static StatusLine Said(string sentence) => new(sentence, StatusOrigin.Told, []);

    /// <summary>Why the page will not act where it is pointed, which answers for no check.</summary>
    public static StatusLine Refused(string sentence) => new(sentence, StatusOrigin.Refusal, []);

    /// <summary>A sentence about an action, with every check <paramref name="verification"/> did not pass.</summary>
    public static StatusLine After(string sentence, VerificationResult verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return new(sentence, StatusOrigin.Outcome, verification.Unpassed);
    }
}

/// <summary>Where the sentence on a <see cref="StatusLine"/> came from.</summary>
public enum StatusOrigin
{
    /// <summary>The page, of its own work: a prompt, a scan's progress or result, a failure.</summary>
    Told,

    /// <summary>
    /// The page, of why it will not act where it is pointed. The one kind a page takes back by
    /// itself, once it is pointed somewhere it will act.
    /// </summary>
    Refusal,

    /// <summary>An action, of what it did, with the checks it did not pass.</summary>
    Outcome,
}
