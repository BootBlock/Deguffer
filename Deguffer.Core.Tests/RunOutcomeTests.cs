using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sentence a finished clean states about itself, which §5.6 requires the user to actually see.
///
/// The defect this covers (issue #41) is that the sentence had no home outside the info bar, and a
/// clean re-plans the list the moment it ends — so every provider that rescan checked reported a
/// progress line into the same bar, and the last one won. A verification failure was on screen for
/// the length of one scan. Deriving the sentence here is what lets the page keep it beside the run's
/// figures instead, where it lasts as long as they do.
/// </summary>
public sealed class RunOutcomeTests
{
    private static CleanupResult Result(
        string name,
        bool verified = true,
        int skipped = 0,
        int kept = 0,
        long reclaimed = 0) => new()
        {
            ProviderId = name.ToLowerInvariant(),
            ProviderName = name,
            Steps = [new StepOutcome("Remove the cache", true, reclaimed, skipped, null, kept)],
            Verification = new VerificationResult
            {
                Checks =
                [
                    new VerificationCheck(
                        @"C:\Users\testuser\.cache\keep-me",
                        "Configuration, not cache",
                        verified,
                        verified ? "Still present." : "MISSING — it was there before the clean."),
                ],
            },
        };

    [Fact]
    public void SaysTheProtectedPathsSurvivedWhenTheyDid()
    {
        var outcome = RunOutcome.For([Result("npm"), Result("NuGet")]);

        Assert.False(outcome.VerificationFailed);
        Assert.Equal("All protected paths survived.", outcome.Statement);
    }

    /// <summary>
    /// One provider over-reaching is the whole run's headline. Reporting the majority that passed
    /// would be true and useless: the user needs to know a rule was over-broad before the next run.
    /// </summary>
    [Fact]
    public void FlagsTheRunWhenAnyOneProviderFailedVerification()
    {
        var outcome = RunOutcome.For([Result("npm"), Result("NuGet", verified: false), Result("pip")]);

        Assert.True(outcome.VerificationFailed);
        Assert.Contains("NuGet", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("survived", outcome.Statement, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every provider that failed is named, because each one is a separate over-broad rule.</summary>
    [Fact]
    public void NamesEveryProviderWhoseVerificationFailed()
    {
        var outcome = RunOutcome.For(
            [Result("npm", verified: false), Result("NuGet"), Result("Gradle", verified: false)]);

        Assert.Contains("npm", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("Gradle", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed run says one thing. Counts of what was left behind are routine, and appending them
    /// to a missing protected path buries the only sentence on the screen that is an alarm.
    /// </summary>
    [Fact]
    public void LeadsWithTheFailureRatherThanWhatTheRunLeftBehind()
    {
        var outcome = RunOutcome.For([Result("npm", verified: false, skipped: 3, kept: 7)]);

        Assert.True(outcome.VerificationFailed);
        Assert.DoesNotContain("left alone", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("too recently", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("please report this", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.3's skipped count and the guard window's kept count are different facts. One is Windows
    /// refusing, which the user can act on by closing something; the other is Deguffer honouring a
    /// setting they chose. Folding them together loses the only difference that matters.
    /// </summary>
    [Fact]
    public void ReportsWhatWasSkippedApartFromWhatWasKeptBack()
    {
        var outcome = RunOutcome.For([Result("npm", skipped: 2), Result("NuGet", kept: 5)]);

        Assert.Contains("2 item(s) in use were left alone.", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("5 file(s) changed too recently to remove.", outcome.Statement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, "All protected paths survived.")]
    [InlineData(4, 0, "All protected paths survived. 4 item(s) in use were left alone.")]
    [InlineData(0, 6, "All protected paths survived. 6 file(s) changed too recently to remove.")]
    public void SaysNothingAboutACountOfZero(int skipped, int kept, string expected)
    {
        Assert.Equal(expected, RunOutcome.For([Result("npm", skipped: skipped, kept: kept)]).Statement);
    }

    /// <summary>
    /// The reclaimed figure is not the sentence's to state. §5.4 puts it on the page under a label
    /// of its own, next to the free-space change it is deliberately kept separate from, and a
    /// sentence repeating it beside that label is one more thing able to contradict it.
    /// </summary>
    [Fact]
    public void LeavesTheReclaimedFigureToTheLabelThatCarriesIt()
    {
        var outcome = RunOutcome.For([Result("npm", reclaimed: 4L * 1024 * 1024 * 1024)]);

        Assert.DoesNotContain("Removed", outcome.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GB", outcome.Statement, StringComparison.Ordinal);
    }
}
