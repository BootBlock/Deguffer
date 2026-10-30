using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a page's status line holds: the sentence, where it came from, and each §5.6 check the
/// sentence counts as not passed, by the path it is about and what it found. A count that asks the
/// user to look at the folders without saying which leaves them nowhere to look.
/// </summary>
public sealed class StatusLineTests
{
    private const string Sentence = "Deleted 'old' (10 B). 1 of 3 check(s) on what should have survived did not pass.";

    private static readonly VerificationResult Mixed = new()
    {
        Checks =
        [
            new(@"C:\Users\testuser\Downloads", "Everything beside the removed item must survive.", VerificationOutcome.Survived, "All 2 other item(s) are still there."),
            new(@"C:\Users\testuser\Linked", "The folder the item was taken out of must survive.", VerificationOutcome.Unverified, "NOT CHECKED — Windows would not describe it."),
            new(@"C:\Users\testuser\Documents", "Everything beside the removed item must survive.", VerificationOutcome.Failed, "MISSING — 1 of 2 other item(s) went too, starting with 'keep.bin'."),
        ],
    };

    /// <summary>
    /// An action's line carries the checks it did not pass, and no other line carries any: a scan's
    /// progress or a refusal that replaces it is not about the folders a removal named, even where
    /// its words are the same.
    /// </summary>
    [Fact]
    public void OnlyAnActionsLineCarriesChecksAndOnlyTheOnesItDidNotPass()
    {
        var after = StatusLine.After(Sentence, Mixed);

        Assert.Equal(StatusOrigin.Outcome, after.Origin);
        Assert.Equal(
            [@"C:\Users\testuser\Documents", @"C:\Users\testuser\Linked"],
            after.Checks.Select(check => check.Subject));

        var said = StatusLine.Said(Sentence);
        Assert.Equal(StatusOrigin.Told, said.Origin);
        Assert.Empty(said.Checks);

        var refused = StatusLine.Refused(Sentence);
        Assert.Equal(StatusOrigin.Refusal, refused.Origin);
        Assert.Empty(refused.Checks);

        Assert.Equal(string.Empty, StatusLine.Empty.Sentence);
        Assert.Empty(StatusLine.Empty.Checks);
    }

    /// <summary>Explore's removal names what its sentence counts.</summary>
    [Fact]
    public void AnExploreRemovalStatesItsSummaryWithWhatDidNotPass()
    {
        var report = new ExploreRemovalReport(ExploreRemovalMode.Permanent, [], Mixed);

        Assert.Equal(report.Summary, report.Statement.Sentence);
        Assert.Equal(StatusOrigin.Outcome, report.Statement.Origin);
        Assert.Equal(Mixed.Unpassed, report.Statement.Checks);
    }

    /// <summary>
    /// A duplicate removal names what its sentence counts, and an answer with no removal names
    /// nothing, because nothing was removed to check.
    /// </summary>
    [Fact]
    public void ADuplicateRemovalStatesWhatDidNotPassAndADeclinedOneNothing()
    {
        var confirmation = new RemovalConfirmation([], 0, 0, string.Empty, [], ExploreRemovalMode.Permanent, []);
        var removed = new DuplicateRemovalAnswer(
            confirmation,
            new DuplicateRemovalReport(ExploreRemovalMode.Permanent, [], Mixed, Cancelled: false));
        var declined = new DuplicateRemovalAnswer(confirmation, Report: null);

        Assert.Equal(removed.Summary, removed.Statement.Sentence);
        Assert.Equal(Mixed.Unpassed, removed.Statement.Checks);
        Assert.Equal(declined.Summary, declined.Statement.Sentence);
        Assert.Empty(declined.Statement.Checks);
    }
}
