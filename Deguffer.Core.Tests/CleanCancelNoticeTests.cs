using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a clean says once Cancel is pressed. A tool that is left to finish can hold the run for
/// minutes, so the press names it and says why the clean has not stopped yet.
/// </summary>
public class CleanCancelNoticeTests
{
    [Fact]
    public void AToolLeftToFinishIsNamedWithWhyTheCleanWaitsForIt() =>
        Assert.Equal(
            "Cancelling. Dism.exe is being left to finish, because stopping it part-way could do harm. "
            + "The clean stops as soon as it exits.",
            CleanCancelNotice.For(new CommandUnderway("Dism.exe", CancelledCommand.Finishes)));

    /// <summary>A tool the cancellation ends holds nothing up, so it is not named as a reason to wait.</summary>
    [Fact]
    public void AToolTheCancellationEndsIsNotNamed() =>
        Assert.Equal("Cancelling the clean…", CleanCancelNotice.For(new CommandUnderway("npm.cmd", CancelledCommand.Ended)));

    [Fact]
    public void ARunWaitingOnNoToolSaysOnlyThatItIsCancelling() =>
        Assert.Equal("Cancelling the clean…", CleanCancelNotice.For(null));
}
