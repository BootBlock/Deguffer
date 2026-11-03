using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>restic cache --cleanup</c>, sent to one cache folder now, would still remove only
/// repository caches holding what restic writes there, with restic itself closed.
///
/// <para><b>restic decides what to remove when it runs.</b> It removes every folder in the cache
/// folder whose name it recognises, so a repository cache that gained a folder of someone else's, a
/// <c>restic check</c> that started, or a link put in place after the preview would be removed with
/// the rest. The run's own look covers only the folders the plan listed, so the cache folder is listed
/// again here (<see cref="ResticCacheLayout.WhyNotWhole"/>).</para>
///
/// <para><b>And restic must not be running.</b> A backup, check or prune that is running writes to
/// the cache the command would remove, which is why the plan offered nothing while restic ran. The
/// user can start one while the preview is on screen.</para>
/// </summary>
/// <param name="root">The cache folder the command is sent to, in display form.</param>
internal sealed class ResticCleanupTarget(string root, IProcessInspector inspector, IReadOnlyList<string> processNames)
    : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        // The planning pass keeps one process snapshot, and the preview's may be past the limit.
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        if (inspector.FindRunning(processNames).Count > 0)
        {
            return "restic is running now, and it uses the cache this command would remove";
        }

        if (LongPath.IsReparsePoint(root))
        {
            return $"{root} is now a link to somewhere else, and restic's cleanup would remove what is on the far side";
        }

        return ResticCacheLayout.WhyNotWhole(root) is { } reason
            ? $"{reason}, and restic's cleanup would remove it with the rest"
            : null;
    }
}
