using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a close says about itself (§7.2.1). The words are Core's, because a report that exists only
/// inside a page is one no test can hold Deguffer to, and this is the surface §5.6 leaves the user.
/// </summary>
public sealed class CloseReportTests
{
    private const long MiB = MemorySnapshotBuilder.MiB;

    private static readonly ProcessMemory Target =
        new(4321, ParentProcessId: 900, "editor.exe", CommitCharge: 200, PrivateWorkingSet: 100, CreationTime: 10);

    private static SystemMemory Machine(long committedMiB) =>
        new MemorySnapshotBuilder().Build().System with { CommitCharge = committedMiB * MiB };

    /// <summary>
    /// §7.2.1: a result that is still watching shows no after figure at all, rather than a
    /// difference that means nothing yet. It also says what Deguffer will do next, which is nothing.
    /// </summary>
    [Fact]
    public void AWatchInProgressHasNoAfterFigureAndPromisesNothingFurther()
    {
        var report = CloseReport.Watching(Target, windows: 2, Machine(12_000));

        Assert.Null(report.After);
        Assert.DoesNotContain("when it exited", report.Figures, StringComparison.Ordinal);
        Assert.Contains("Each of its 2 windows was asked.", report.Statement, StringComparison.Ordinal);
        Assert.Contains("send nothing else", report.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The figure §7.2.1 reports, and the sentence that keeps it honest: Windows goes on allocating
    /// and freeing throughout a watch that lasts as long as somebody takes to answer a save prompt,
    /// so the difference is what happened rather than what this close returned.
    /// </summary>
    [Fact]
    public void AClosedProgramReportsCommitChargeBeforeAndAfterAndSaysWhatTheDifferenceIs()
    {
        var report = CloseReport.Closed(
            Target, windows: 1, Machine(12_000), Machine(11_000), new VerificationResult());

        Assert.Contains(FreeSpace.Format(12_000 * MiB), report.Figures, StringComparison.Ordinal);
        Assert.Contains(FreeSpace.Format(11_000 * MiB), report.Figures, StringComparison.Ordinal);
        Assert.Contains("when the close was sent", report.Figures, StringComparison.Ordinal);
        Assert.Contains("when it exited", report.Figures, StringComparison.Ordinal);
        Assert.Contains("went on allocating and freeing", report.Figures, StringComparison.Ordinal);
        Assert.Contains("closed", report.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2.1: a program that is still running is reported, not escalated. The report says it may
    /// have refused, and offers nothing stronger, because there is nothing stronger to offer.
    /// </summary>
    [Fact]
    public void AProgramStillRunningIsReportedAndNothingStrongerIsOffered()
    {
        var report = CloseReport.StillRunning(Target, windows: 1, Machine(12_000), new VerificationResult());

        Assert.Null(report.After);
        Assert.Contains("still running", report.Statement, StringComparison.Ordinal);
        Assert.Contains("may have refused", report.Statement, StringComparison.Ordinal);
        Assert.Contains("pick it again", report.Statement, StringComparison.Ordinal);

        foreach (var never in new[] { "terminate", "force", "end task", "kill" })
        {
            Assert.DoesNotContain(never, report.Statement, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A §5.6 assertion that did not pass reaches the sentence itself. Whether the user is told that
    /// something this close could not have ended is missing must not depend on which surface is
    /// rendering the report, or on the user scrolling a list of checks.
    /// </summary>
    [Fact]
    public void AFailedAssertionIsInTheSentenceAndNotOnlyInTheChecks()
    {
        var verification = new VerificationResult
        {
            Checks =
            [
                new VerificationCheck(
                    "explorer.exe (process 100)",
                    "Asking a program to close cannot end the desktop.",
                    VerificationOutcome.Failed,
                    "MISSING — it was running when the close was posted."),
            ],
        };

        var report = CloseReport.Closed(Target, windows: 1, Machine(12_000), Machine(11_000), verification);

        Assert.Contains("did not pass", report.Statement, StringComparison.Ordinal);
        Assert.Contains("Look at the report", report.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process with no window that qualifies is refused rather than attempted (§7.2.1), so a report
    /// about a close that asked nothing is a report that should never have been built.
    /// </summary>
    [Fact]
    public void ACloseThatAskedNoWindowHasNoReport() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CloseReport.Watching(Target, windows: 0, Machine(12_000)));
}
