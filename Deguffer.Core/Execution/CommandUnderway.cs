using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The tool a clean is waiting on while one of its commands runs. Reported when the command starts
/// and cleared, with null, when it returns, so a page can say what a cancellation pressed now would
/// wait for. See <see cref="CleanCancelNotice"/>.
/// </summary>
/// <param name="Tool">The program's file name, as the step's description names it.</param>
/// <param name="WhenCancelled">What a cancellation does to it, as the provider decided.</param>
public sealed record CommandUnderway(string Tool, CancelledCommand WhenCancelled)
{
    public static CommandUnderway Of(RunCommandStep step) => new(Path.GetFileName(step.FileName), step.WhenCancelled);
}
