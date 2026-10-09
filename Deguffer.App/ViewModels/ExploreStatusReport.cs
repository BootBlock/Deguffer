using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// A sentence for Explore's status line, with the §5.6 checks it has to answer for, which the page
/// lists beside it. Together, because the list says which folders the sentence asks the user to look
/// at, and a list left standing beside a later sentence would name folders that sentence never meant.
/// </summary>
/// <param name="Checks">Every check that did not pass (<see cref="VerificationResult.Unpassed"/>); empty for anything but a removal's report.</param>
public sealed record ExploreStatusReport(string Sentence, IReadOnlyList<VerificationCheck> Checks)
{
    /// <summary>A sentence that answers for no check.</summary>
    public static ExploreStatusReport Said(string sentence) => new(sentence, []);
}
