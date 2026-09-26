using System.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the report says once a press on Memory's one action is over (§7.2.1). Which of these a user
/// reads decides what they believe happened to another program's unsaved work, so each is told apart
/// from the others by its words.
/// </summary>
public sealed class CloseOutcomeTests
{
    private static readonly ProcessMemory Target =
        new(4321, ParentProcessId: 900, "editor.exe", CommitCharge: 200, PrivateWorkingSet: 100, CreationTime: 10);

    private static SystemMemory Machine() => new MemorySnapshotBuilder().Build().System;

    /// <summary>
    /// Declining is a decision, and the report says nothing was sent rather than leaving the previous
    /// sentence standing, which somebody who has just dismissed a dialog reads as its outcome.
    /// </summary>
    [Fact]
    public void ADeclinedConfirmationSaysThatNothingWasSent()
    {
        var outcome = CloseOutcome.Of(Target, attempt: null);

        Assert.Null(outcome.Report);
        Assert.Equal("editor.exe (process 4321) was not asked to close. Nothing was sent.", outcome.Statement);
    }

    [Fact]
    public void ACloseThatWentAheadIsReportedWithItsEvidence()
    {
        var report = CloseReport.Closed(Target, windows: 1, Machine(), Machine(), new VerificationResult());

        var outcome = CloseOutcome.Of(Target, new CloseAttempt(MemoryVerdict.Allow([]), report));

        Assert.Same(report, outcome.Report);
        Assert.Equal(report.Statement, outcome.Statement);
    }

    /// <summary>
    /// The second decision, made with the handle held, is the answer: the machine moved between the
    /// pick and the confirmation, so what the page said then is out of date.
    /// </summary>
    [Fact]
    public void ACloseRefusedAtTheMomentOfTheActionSaysWhyAndReportsNoClose()
    {
        var refused = MemoryVerdict.Refuse("The program you picked has gone.");

        var outcome = CloseOutcome.Of(Target, new CloseAttempt(refused, Report: null));

        Assert.Null(outcome.Report);
        Assert.Equal(refused.Reason, outcome.Statement);
    }

    [Fact]
    public void APressTheVerdictRefusesIsAnsweredWithTheVerdictsOwnReason()
    {
        var outcome = CloseOutcome.Refused(MemoryTarget.NotAProgram);

        Assert.Null(outcome.Report);
        Assert.Equal(MemoryTarget.NotAProgram.Reason, outcome.Statement);
    }

    /// <summary>Everything that can fail this way happens before the first message, and the words say so.</summary>
    [Fact]
    public void ACallWindowsWouldNotAnswerSaysWhichAndThatNothingWasAsked()
    {
        var failure = new Win32Exception(5);

        var outcome = CloseOutcome.Unanswered(failure);

        Assert.Null(outcome.Report);
        Assert.Equal($"Windows would not answer: {failure.Message} Nothing was asked to close.", outcome.Statement);
    }

    /// <summary>
    /// A policy nobody could run refuses, as every other unanswered fact does: a program nothing could
    /// be established about is not one to ask.
    /// </summary>
    [Fact]
    public void AnUnansweredPolicyRefuses()
    {
        Assert.False(MemoryActionPolicy.Unanswered.IsAllowed);
        Assert.Empty(MemoryActionPolicy.Unanswered.Windows);
        Assert.Contains("will not ask this one", MemoryActionPolicy.Unanswered.Reason, StringComparison.Ordinal);
    }
}
