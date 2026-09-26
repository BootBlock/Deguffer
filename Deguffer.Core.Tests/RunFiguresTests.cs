using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>§5.4: the figures a clean reports, each kept apart from the others.</summary>
public sealed class RunFiguresTests
{
    private static CleanupResult Result(string id, params StepOutcome[] steps) => new()
    {
        ProviderId = id,
        ProviderName = id,
        Steps = steps,
    };

    private static StepOutcome Step(long removed = 0, long entries = 0, long requested = 0, long scheduled = 0) =>
        new("A step", Succeeded: true, removed, Refusals.None, EntriesRemoved: entries, BytesRequested: requested, BytesScheduled: scheduled);

    private static VerificationCheck Check(string subject, VerificationOutcome outcome) =>
        new($@"C:\Users\testuser\{subject}", "Protected.", outcome, "Detail.");

    [Fact]
    public void EachFigureIsSummedOverTheWholeRunAndKeptApart()
    {
        var figures = RunFigures.For(
        [
            Result("npm", Step(removed: 100, entries: 4), Step(requested: 30)),
            Result("vs", Step(removed: 50, entries: 1, scheduled: 7)),
        ]);

        Assert.Equal(150, figures.Removed.Reclaimable);
        Assert.Equal(5, figures.Removed.Entries);
        Assert.Equal(30, figures.Requested);
        Assert.Equal(7, figures.Scheduled);
    }

    /// <summary>A clean that took only empty leftovers removed entries, which is what the card then says.</summary>
    [Fact]
    public void ARunThatFreedNoBytesStillCountsWhatItRemoved()
    {
        var figures = RunFigures.For([Result("leftovers", Step(entries: 12))]);

        Assert.Equal(0, figures.Removed.Reclaimable);
        Assert.Equal(12, figures.Removed.Entries);
    }

    /// <summary>
    /// Every check that is not a pass is listed, and only those: each is a path nobody could vouch
    /// for, and the sentence above asks the user to report something only this list describes.
    /// </summary>
    [Fact]
    public void ListsEveryCheckThatIsNotAPassAndNoPass()
    {
        var figures = RunFigures.For(
        [
            Result("npm") with
            {
                Verification = new VerificationResult
                {
                    Checks =
                    [
                        Check("survived", VerificationOutcome.Survived),
                        Check("failed", VerificationOutcome.Failed),
                        Check("emptied", VerificationOutcome.Emptied),
                        Check("outside", VerificationOutcome.RemovedFromOutside),
                        Check("unverified", VerificationOutcome.Unverified),
                        Check("absent", VerificationOutcome.NotPresentBefore),
                    ],
                },
            },
            Result("unverified-provider"),
        ]);

        Assert.Equal(
            [@"C:\Users\testuser\failed", @"C:\Users\testuser\emptied", @"C:\Users\testuser\outside", @"C:\Users\testuser\unverified"],
            figures.Checks.Select(c => c.Subject));
    }

    [Fact]
    public void TheFreeSpaceChangeIsSignedAndNeedsBothReadings()
    {
        Assert.Equal(-500, RunFigures.FreeSpaceChange(before: 1_000, after: 500));
        Assert.Equal(250, RunFigures.FreeSpaceChange(before: 1_000, after: 1_250));
        Assert.Null(RunFigures.FreeSpaceChange(before: null, after: 500));
        Assert.Null(RunFigures.FreeSpaceChange(before: 1_000, after: null));
    }
}
