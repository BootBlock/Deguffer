using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The surface a close's report stands on until the user takes it down (§7.2.1). Its words are
/// <see cref="CloseReport"/>'s; what is proven here is which of its states the page shows, because
/// one of them withholds the second close and another colours the sentence.
/// </summary>
public sealed class MemoryCloseReportTests
{
    private static readonly ProcessMemory Target =
        new(4321, ParentProcessId: 900, "editor.exe", CommitCharge: 200, PrivateWorkingSet: 100, CreationTime: 10);

    private static SystemMemory Machine() => new MemorySnapshotBuilder().Build().System;

    private static VerificationResult Lost() => new()
    {
        Checks = [new VerificationCheck("explorer.exe (process 100)", "The shell", VerificationOutcome.Failed, "Gone.")],
    };

    [Fact]
    public void AWatchInProgressIsShownAsWatching()
    {
        var report = new MemoryCloseReport();

        report.Show(CloseReport.Watching(Target, windows: 1, Machine()));

        Assert.True(report.IsWatching);
        Assert.False(report.Failed);
        Assert.True(report.HasReport);
    }

    [Fact]
    public void AFinishedCloseIsNoLongerWatched()
    {
        var report = new MemoryCloseReport();
        report.Show(CloseReport.Watching(Target, windows: 1, Machine()));

        report.Show(CloseReport.StillRunning(Target, windows: 1, Machine(), new VerificationResult()));

        Assert.False(report.IsWatching);
        Assert.True(report.HasReport);
    }

    /// <summary>A §5.6 check that did not pass colours the sentence, and its line is on the report.</summary>
    [Fact]
    public void ACloseThatLostSomethingItMustNotHaveIsShownAsFailed()
    {
        var report = new MemoryCloseReport();

        report.Show(CloseReport.Closed(Target, windows: 1, Machine(), Machine(), Lost()));

        Assert.True(report.Failed);
        Assert.True(report.HasChecks);
        Assert.Single(report.Checks);
    }

    /// <summary>A sentence with no close behind it has nothing to assert and no figure to take.</summary>
    [Fact]
    public void ASentenceClearsWhateverCloseWasShownBefore()
    {
        var report = new MemoryCloseReport();
        report.Show(CloseReport.Closed(Target, windows: 1, Machine(), Machine(), Lost()));

        report.Say("Nothing was sent.");

        Assert.Equal("Nothing was sent.", report.Statement);
        Assert.False(report.Failed);
        Assert.False(report.IsWatching);
        Assert.False(report.HasFigures);
        Assert.False(report.HasChecks);
    }

    /// <summary>Taking the report down is one of the watch's three ends, so the owner is told once.</summary>
    [Fact]
    public void DismissingTakesTheReportDownAndSaysSoOnce()
    {
        var report = new MemoryCloseReport();
        report.Show(CloseReport.Watching(Target, windows: 1, Machine()));
        var dismissed = 0;
        report.Dismissed += (_, _) => dismissed++;

        report.DismissCommand.Execute(null);

        Assert.Equal(1, dismissed);
        Assert.False(report.HasReport);
        Assert.False(report.IsWatching);
    }
}
