namespace Deguffer.Core.Execution;

/// <summary>
/// How long one step of a clean took, with the two waits that come before its work apart from the
/// work itself.
///
/// <para><b>Apart because they are slow for different reasons.</b> A use check asks the process table
/// and every open handle, a look walks a whole tree for an Outlook data file, and the work is the
/// removal or the tool. A step that waited on the first two looks the same as one that removed a
/// large tree unless they are told apart.</para>
/// </summary>
/// <param name="Elapsed">The whole step, from the first question asked of it to its outcome.</param>
/// <param name="UseCheck">
/// Asking whether anything is using what the step would remove, including asking a program to let go
/// first. Zero for a step that carries no use check.
/// </param>
/// <param name="Look">
/// The look at the whole tree on the disk immediately before a removal that cannot do less. See
/// <see cref="WholeTreeLook"/>. Zero where none was taken.
/// </param>
public readonly record struct StepTiming(TimeSpan Elapsed, TimeSpan UseCheck, TimeSpan Look)
{
    /// <summary>
    /// Everything else: the removal, the command or the emptying, with the measurements and checks
    /// around it. For a directory step, <see cref="StepOutcome.Passes"/> says where its removal spent
    /// this.
    /// </summary>
    public TimeSpan Acting => Elapsed - UseCheck - Look;
}
