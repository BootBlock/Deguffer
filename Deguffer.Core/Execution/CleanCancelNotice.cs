using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a clean says from the moment Cancel is pressed until the run reports how it ended.
///
/// <para>A command that is <see cref="CancelledCommand.Finishes"/> can hold the run for many minutes
/// after the press: DISM's component store reset is never ended part-way (§5.1). Without this the page
/// went on reading as a clean in progress, and nothing said the press was heard or why the clean had
/// not stopped, until the tool exited and the step's result explained it.</para>
/// </summary>
public static class CleanCancelNotice
{
    /// <param name="underway">The command the run is waiting on, or null where it is waiting on none.</param>
    public static string For(CommandUnderway? underway) =>
        underway is { WhenCancelled: CancelledCommand.Finishes }
            ? $"Cancelling. {underway.Tool} is being left to finish, because stopping it part-way could do harm. "
              + "The clean stops as soon as it exits."
            : "Cancelling the clean…";
}
