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
        VerificationOutcome outcome = VerificationOutcome.Survived,
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
                        outcome,
                        "Whatever the check found."),
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
        var outcome = RunOutcome.For([Result("npm"), Result("NuGet", VerificationOutcome.Failed), Result("pip")]);

        Assert.True(outcome.VerificationFailed);
        Assert.Contains("NuGet", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("survived", outcome.Statement, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every provider that failed is named, because each one is a separate over-broad rule.</summary>
    [Fact]
    public void NamesEveryProviderWhoseVerificationFailed()
    {
        var outcome = RunOutcome.For(
            [Result("npm", VerificationOutcome.Failed), Result("NuGet"), Result("Gradle", VerificationOutcome.Failed)]);

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
        var outcome = RunOutcome.For([Result("npm", VerificationOutcome.Failed, skipped: 3, kept: 7)]);

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

    /// <summary>
    /// A protected path taken by something else while the preview sat on screen. It has to be said
    /// — the run's figures describe a machine that moved — and it must not be said as an alarm. The
    /// sentence that asks the user to report a fault is the one thing that stops meaning anything if
    /// it is stated about an ordinary event.
    /// </summary>
    [Fact]
    public void SaysAPathWentFromOutsideTheRunWithoutCallingItAFailure()
    {
        var outcome = RunOutcome.For(
            [Result("npm"), Result(".NET intermediate build output", VerificationOutcome.RemovedFromOutside)]);

        Assert.Equal(RunVerdict.RemovedFromOutside, outcome.Verdict);
        Assert.False(outcome.VerificationFailed);
        Assert.True(outcome.NeedsReporting);

        Assert.Contains(".NET intermediate build output", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("Preview again", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("please report this", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("All protected paths survived", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that verified cleanly leaves the bar to the fresh preview's totals, which describe the
    /// list now on screen. Only the two verdicts with something to answer for hold it.
    /// </summary>
    [Theory]
    [InlineData(VerificationOutcome.Survived, false)]
    [InlineData(VerificationOutcome.NotPresentBefore, false)]
    [InlineData(VerificationOutcome.RemovedFromOutside, true)]
    [InlineData(VerificationOutcome.Failed, true)]
    public void OnlyAVerdictWithSomethingToAnswerForHoldsTheInfoBar(
        VerificationOutcome outcome,
        bool expected)
    {
        Assert.Equal(expected, RunOutcome.For([Result("npm", outcome)]).NeedsReporting);
    }

    /// <summary>
    /// One over-broad rule outranks any number of paths that went on their own. Leading with the
    /// milder sentence would leave the alarm unsaid.
    /// </summary>
    [Fact]
    public void AFailureOutranksAPathTakenFromOutside()
    {
        var outcome = RunOutcome.For(
            [Result("npm", VerificationOutcome.RemovedFromOutside), Result("NuGet", VerificationOutcome.Failed)]);

        Assert.Equal(RunVerdict.VerificationFailed, outcome.Verdict);
        Assert.Contains("NuGet", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("npm", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the run left behind stays on this sentence, unlike the failure's. It is not an alarm to
    /// bury, and both facts explain the same thing: why the figures are not what the preview said.
    /// </summary>
    [Fact]
    public void KeepsWhatTheRunLeftBehindBesideAnOutsideRemoval()
    {
        var outcome = RunOutcome.For(
            [Result("npm", VerificationOutcome.RemovedFromOutside, skipped: 2, kept: 5)]);

        Assert.Contains("2 item(s) in use were left alone.", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("5 file(s) changed too recently to remove.", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is of paths rather than of providers. One removed checkout takes a protected path
    /// per project inside it, and "1 protected path" about nine of them is a figure the user cannot
    /// reconcile with the list beneath it.
    /// </summary>
    [Fact]
    public void CountsThePathsRatherThanTheProviders()
    {
        var result = Result("npm", VerificationOutcome.RemovedFromOutside);
        var three = result with
        {
            Verification = new VerificationResult
            {
                Checks = [.. Enumerable.Range(0, 3).Select(i => new VerificationCheck(
                    $@"C:\Users\testuser\src\project{i}\obj",
                    "It must survive.",
                    VerificationOutcome.RemovedFromOutside,
                    "Whatever the check found."))],
            },
        };

        Assert.Contains("3 protected path(s)", RunOutcome.For([three]).Statement, StringComparison.Ordinal);
    }
}
