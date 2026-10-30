using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>conan cache clean -s -b -t</c> still removes only what the plan meant: true while no
/// Conan is running, and false from the moment one starts.
///
/// <para><b>Why the plan's own look is not enough.</b> <c>-t</c> removes every folder of <c>b</c>
/// without a finished package in it, and a <c>conan create</c> started after the preview has just
/// such a folder, its build in progress. The same arguments would then take a build the plan never
/// saw, and §5.6 could not notice, because a folder the plan never listed is not one it protects.
/// Conan's cache is not safe for concurrent use besides.</para>
/// </summary>
/// <param name="Inspector">Asked afresh at the clean, to within <see cref="IUseCheck.ReadingLimit"/>.</param>
/// <param name="Names">The process names Conan runs as.</param>
internal sealed record ConanNotRunning(IProcessInspector Inspector, IReadOnlyList<string> Names) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        Inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        return Inspector.FindRunning(Names) is { Count: > 0 } running
            ? $"{string.Join(", ", running)} is running now, and Conan's clean removes unfinished builds, "
                + "which a build in progress is"
            : null;
    }
}
