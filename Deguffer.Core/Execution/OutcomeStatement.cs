namespace Deguffer.Core.Execution;

/// <summary>
/// A sentence a page shows after acting, with the §5.6 checks it has to answer for, which the page
/// lists beside it.
///
/// <para>The sentence counts the checks that did not pass and asks the user to look at the folders,
/// and after a removal that touched many of them a count does not say which. The list says which,
/// so it belongs to the sentence: one left standing beside a later sentence, a scan's progress or a
/// refusal, would name folders that sentence never meant.</para>
/// </summary>
/// <param name="Checks">Every check that did not pass (<see cref="VerificationResult.Unpassed"/>), or none.</param>
public sealed record OutcomeStatement(string Sentence, IReadOnlyList<VerificationCheck> Checks)
{
    /// <summary>A sentence that answers for no check.</summary>
    public static OutcomeStatement Said(string sentence) => new(sentence, []);

    /// <summary>A sentence about an action, with every check <paramref name="verification"/> did not pass.</summary>
    public static OutcomeStatement After(string sentence, VerificationResult verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return new(sentence, verification.Unpassed);
    }

    /// <summary>
    /// The checks to list while the page's line reads <paramref name="shown"/>: these, while it is
    /// still this sentence, and none once anything else has replaced it.
    /// </summary>
    public IReadOnlyList<VerificationCheck> ChecksBeside(string shown) =>
        string.Equals(shown, Sentence, StringComparison.Ordinal) ? Checks : [];
}
