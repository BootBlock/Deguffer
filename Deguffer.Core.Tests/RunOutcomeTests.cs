using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

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
        Refusals refused = default,
        int kept = 0,
        long reclaimed = 0) => new()
        {
            ProviderId = name.ToLowerInvariant(),
            ProviderName = name,
            Steps = [new StepOutcome("Remove the cache", true, reclaimed, refused, null, kept)],
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

    private static Refusals InUse(int files, long bytes) => new(new RefusalTally(files, bytes), default);

    private static Refusals Denied(int files, long bytes) => new(default, new RefusalTally(files, bytes));

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
    /// A failed run says one thing. What was left behind is routine, and appending it to a missing
    /// protected path buries the only sentence on the screen that is an alarm.
    /// </summary>
    [Fact]
    public void LeadsWithTheFailureRatherThanWhatTheRunLeftBehind()
    {
        var outcome = RunOutcome.For(
            [Result("npm", VerificationOutcome.Failed, InUse(3, 3000) + Denied(4, 4000), kept: 7)]);

        Assert.True(outcome.VerificationFailed);
        Assert.DoesNotContain("left in place", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("would not let Deguffer", outcome.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("too recently", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("please report this", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// What Windows would not release and what the guard window kept are different facts. One is
    /// Windows declining, and the other is Deguffer honouring a setting the user chose. Folding them
    /// together loses the only difference that matters.
    /// </summary>
    [Fact]
    public void ReportsWhatWindowsRefusedApartFromWhatWasKeptBack()
    {
        var outcome = RunOutcome.For([Result("npm", refused: InUse(2, 2048)), Result("NuGet", kept: 5)]);

        Assert.Contains(
            $"Another program had 2 file(s) ({FreeSpace.Format(2048)}) open, so they were left in place.",
            outcome.Statement,
            StringComparison.Ordinal);
        Assert.Contains("5 file(s) changed too recently to remove.", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect in issue #117, in the words it produced: 5.9 GB that Windows would not
    /// let go was reported as "252994 item(s) in use were left alone", which reads as a handful of
    /// locked files — and the next preview offered all of it again. A denial is not a file in use,
    /// its size is what tells the reader the run fell short, and the reader is owed the answer to
    /// whether the row will keep offering it.
    /// </summary>
    [Fact]
    public void SaysHowMuchWindowsWouldNotLetGoAndWhatTheNextPreviewDoesWithIt()
    {
        const long guarded = 6_340_000_000;

        var statement = RunOutcome.For([Result("Temporary files", refused: Denied(252, guarded))]).Statement;

        Assert.Contains(
            $"Windows would not let Deguffer remove 252 file(s) ({FreeSpace.Format(guarded)}).",
            statement,
            StringComparison.Ordinal);
        Assert.Contains("The next preview leaves out whatever is still refused.", statement, StringComparison.Ordinal);

        Assert.DoesNotContain("in use", statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Another program", statement, StringComparison.Ordinal);
    }

    /// <summary>Both kinds together are both stated, and the promise about the next preview once.</summary>
    [Fact]
    public void StatesBothKindsOfRefusalAndThePromiseOnce()
    {
        var statement = RunOutcome.For([Result("npm", refused: InUse(1, 10)), Result("NuGet", refused: Denied(2, 20))]).Statement;

        Assert.Contains("Another program had 1 file(s)", statement, StringComparison.Ordinal);
        Assert.Contains("Windows would not let Deguffer remove 2 file(s)", statement, StringComparison.Ordinal);
        Assert.Equal(1, statement.Split("The next preview").Length - 1);
    }

    [Fact]
    public void SaysNothingAboutARefusalOrAKeepOfZero()
    {
        Assert.Equal("All protected paths survived.", RunOutcome.For([Result("npm")]).Statement);

        Assert.Equal(
            "All protected paths survived. 6 file(s) changed too recently to remove.",
            RunOutcome.For([Result("npm", kept: 6)]).Statement);
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
            [Result("npm", VerificationOutcome.RemovedFromOutside, InUse(2, 200), kept: 5)]);

        Assert.Contains("Another program had 2 file(s)", outcome.Statement, StringComparison.Ordinal);
        Assert.Contains("5 file(s) changed too recently to remove.", outcome.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is of paths rather than of providers. One removed checkout takes a protected path
    /// per project inside it, and "one protected path" about nine of them is a figure the user
    /// cannot reconcile with the list beneath it.
    /// </summary>
    [Fact]
    public void CountsThePathsRatherThanTheProviders()
    {
        Assert.Contains(
            "3 protected paths",
            RunOutcome.For([OutsideRemovals("npm", 3)]).Statement,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Written out in both grammatical forms rather than with the "(s)" the counts elsewhere use:
    /// this clause has to agree in "it" and "them" as well, and "1 protected path(s) … the folders
    /// holding them" is a sentence only a machine writes. Driving the window is what catches it.
    /// </summary>
    [Fact]
    public void ReadsAsEnglishForOnePathAsWellAsForSeveral()
    {
        var one = RunOutcome.For([OutsideRemovals("npm", 1)]).Statement;

        Assert.Contains("One protected path for npm went missing", one, StringComparison.Ordinal);
        Assert.Contains("the folder holding it", one, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", one, StringComparison.Ordinal);

        var several = RunOutcome.For([OutsideRemovals("npm", 2)]).Statement;

        Assert.Contains("2 protected paths for npm went missing", several, StringComparison.Ordinal);
        Assert.Contains("the folders holding them", several, StringComparison.Ordinal);
    }

    /// <summary>One provider's result carrying <paramref name="count"/> paths taken from outside.</summary>
    private static CleanupResult OutsideRemovals(string name, int count) =>
        Result(name, VerificationOutcome.RemovedFromOutside) with
        {
            Verification = new VerificationResult
            {
                Checks = [.. Enumerable.Range(0, count).Select(i => new VerificationCheck(
                    $@"C:\Users\testuser\src\project{i}\obj",
                    "It must survive.",
                    VerificationOutcome.RemovedFromOutside,
                    "Whatever the check found."))],
            },
        };
}
