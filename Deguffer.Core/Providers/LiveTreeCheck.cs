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
/// <para>An answer that could not be had in full is read as the plan read it. Where the plan offered
/// the directory on the same partial answer and said so in a note, the step runs: holding it back
/// here would refuse at the clean what the preview offered, for no new reason. Where the plan held
/// every directory back on it instead, <c>unknown</c> says why, and the step is held back too.</para>
/// </summary>
/// <param name="query">What the veto asked about this directory when the plan was made.</param>
/// <param name="unknown">
/// Why the step is held back where the inspector could not tell, as an <see cref="InUseNow.Reason"/>,
/// for a plan that refused on that answer. Null for one that offered on it.
/// </param>
internal sealed class LiveTreeCheck(ILiveTreeInspector inspector, LiveTreeQuery query, string? unknown = null) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass. That table is the preview's.
        inspector.Invalidate();

        var findings = inspector.FindLive([query], ct);

        IReadOnlyList<InUseNow> live =
        [
            .. findings.Live
                .Where(tree => tree.Directory.Equals(query.Directory, StringComparison.OrdinalIgnoreCase))
                .Select(tree => new InUseNow(step.Path, string.Join("; ", tree.Holders))),
        ];

        return live.Count == 0 && !findings.Complete && unknown is not null
            ? [new InUseNow(step.Path, unknown)]
            : live;
    }
}
