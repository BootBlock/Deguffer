using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a §5.6 result says when its subject acts on its own.
///
/// <para>A disk does not delete itself while Deguffer looks away, so every check a clean makes is a
/// claim that either holds or fails. §7.2.1's close cannot work that way: processes exit constantly,
/// a closed program's children are expected to go with it, and a service host can exit because the
/// program that was its last client closed. So the close records what it sent and lists those exits
/// beside the two claims it can make, and this result has to carry all four without turning a list
/// into an alarm or into a path it never checked.</para>
/// </summary>
public sealed class VerificationResultTests
{
    /// <summary>
    /// The denominator is what was asserted. Counting the record of what Deguffer sent, and every
    /// process that exited while the watch was open, would say "all 40 survived" about a run that
    /// checked two things.
    /// </summary>
    [Fact]
    public void WhatACloseRecordsIsNotCountedAsSomethingThatSurvived()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Survived, "dwm.exe (process 1200)"),
                Check(VerificationOutcome.Sent, "window 0x00040122 of notepad.exe (process 4321)"),
                Check(VerificationOutcome.ExpectedExit, "helper.exe (process 4400)"),
                Check(VerificationOutcome.UnclaimedExit, "svchost.exe (process 980)"),
            ],
        };

        Assert.Equal("All 1 protected item(s) survived.", result.Summary);
    }

    /// <summary>
    /// §7.2.1: a child of the closed program going with it is the program closing properly, and an
    /// exit Deguffer sent nothing to is one it does not account for. Failing a run over either would
    /// put a false alarm on the one surface §5.6 exists to make trustworthy.
    /// </summary>
    [Fact]
    public void AnExitTheCloseExpectedOrDoesNotClaimLeavesTheRunPassing()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Sent, "window 0x00040122 of notepad.exe (process 4321)"),
                Check(VerificationOutcome.ExpectedExit, "helper.exe (process 4400)"),
                Check(VerificationOutcome.UnclaimedExit, "svchost.exe (process 980)"),
            ],
        };

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// The claim §7.2.1 does make: asking an ordinary program to close cannot end the shell or the
    /// compositor, so one of those missing afterwards is the alarm, and the row names which.
    /// </summary>
    [Fact]
    public void AProcessTheCloseCouldNotHaveEndedGoingMissingFailsTheRun()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Sent, "window 0x00040122 of notepad.exe (process 4321)"),
                Check(VerificationOutcome.Failed, "explorer.exe (process 6100)"),
            ],
        };

        Assert.False(result.Passed);
        Assert.Equal("explorer.exe (process 6100)", Assert.Single(result.Failures).Subject);
        Assert.Equal("1 of 1 protected item(s) did not survive.", result.Summary);
    }

    /// <summary>
    /// What a page lists beside its sentence after a removal (#301): every check the sentence counts
    /// as not passed, the alarms first, and nothing that passed or asserts nothing. A list that left
    /// out the checks Windows would not let be made would name fewer folders than the sentence
    /// counts, and one that took in a pass would send the user to a folder that is fine.
    /// </summary>
    [Fact]
    public void WhatDidNotPassIsEveryCheckThatDidNotPassAndNoOther()
    {
        var result = new VerificationResult
        {
            Checks =
            [
                Check(VerificationOutcome.Unverified, @"C:\Users\testuser\Linked"),
                Check(VerificationOutcome.Survived, @"C:\Users\testuser\Downloads"),
                Check(VerificationOutcome.RemovedFromOutside, @"C:\Users\testuser\.gradle\gradle.properties"),
                Check(VerificationOutcome.NotPresentBefore, @"C:\Users\testuser\Absent"),
                Check(VerificationOutcome.Emptied, @"C:\Users\testuser\.m2\settings"),
                Check(VerificationOutcome.Sent, "window 0x00040122 of notepad.exe (process 4321)"),
                Check(VerificationOutcome.Failed, @"C:\Users\testuser\Documents"),
                Check(VerificationOutcome.ExpectedExit, "helper.exe (process 4400)"),
                Check(VerificationOutcome.Entered, @"C:\Users\testuser\.cargo\config"),
                Check(VerificationOutcome.UnclaimedExit, "svchost.exe (process 980)"),
            ],
        };

        Assert.Equal(
            [
                @"C:\Users\testuser\.m2\settings",
                @"C:\Users\testuser\Documents",
                @"C:\Users\testuser\.cargo\config",
                @"C:\Users\testuser\.gradle\gradle.properties",
                @"C:\Users\testuser\Linked",
            ],
            result.Unpassed.Select(check => check.Subject));
    }

    private static VerificationCheck Check(VerificationOutcome outcome, string subject) =>
        new(subject, "It must survive.", outcome, "Whatever was found.");
}
