using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// <see cref="LiveTreeVeto"/> asked again immediately before the directory it cleared is removed: a
/// program running from inside it, a program working in its project, or a declared lock file held open.
///
/// <para><b>This is the case the question exists for.</b> A preview found nothing using a project, and
/// the user opened it in an editor, or started a build, before pressing Clean. A build directory
/// removed under a live editor or a build in flight breaks the work in progress, which is why the
/// veto refuses rather than warns, and a refusal made at the preview says nothing about the clean.</para>
///
/// <para>A check that could not see every process's working directory lets the step run, because the
/// plan offered it on the same partial answer and said so in a note. Holding it back here would refuse
/// at the clean what the preview offered, for no new reason.</para>
/// </summary>
/// <param name="query">What the veto asked about this directory when the plan was made.</param>
internal sealed class LiveTreeCheck(ILiveTreeInspector inspector, LiveTreeQuery query) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass. That table is the preview's.
        inspector.Invalidate();

        return
        [
            .. inspector.FindLive([query], ct).Live
                .Where(live => live.Directory.Equals(query.Directory, StringComparison.OrdinalIgnoreCase))
                .Select(live => new InUseNow(step.Path, string.Join("; ", live.Holders))),
        ];
    }
}
