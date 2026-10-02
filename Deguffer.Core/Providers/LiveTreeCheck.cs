using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// <see cref="LiveTreeVeto"/> asked again immediately before the directory it cleared is removed: a
/// program running from inside it, a program working in its project or beside a solution that names
/// it, or a declared lock file held open.
///
/// <para><b>This is the case the question exists for.</b> A preview found nothing using a project, and
/// the user opened it in an editor, or started a build, before pressing Clean. A build directory
/// removed under a live editor or a build in flight breaks the work in progress, which is why the
/// veto refuses rather than warns, and a refusal made at the preview says nothing about the clean.</para>
///
/// <para>An answer that could not be had in full holds the step back where the plan's was whole, and
/// lets it run where the plan offered it on the same partial answer. See
/// <see cref="LiveTreeVeto.AtClean"/>.</para>
///
/// <para>The directory asked about need not be the step's. A superseded Squirrel build is held by the
/// application running from the build beside it, so the veto asks about the installation, and what it
/// finds holds back the step that removes the build.</para>
/// </summary>
/// <param name="directory">The directory the veto asked about, and the project it belongs to.</param>
/// <param name="questions">
/// The veto's own rule for what to ask, applied again rather than its answer remembered. Unreal's
/// logs are found by listing the project's <c>Saved\Logs</c>, and an editor opened on the project
/// after the preview may have written the first one there. A solution's folder is a workspace only
/// while a program is in it and only for the projects the solution names now, and Visual Studio
/// opened after the preview, or a project added to its solution since, is the one this is for.
/// </param>
/// <param name="planComplete">Whether the veto's answer the plan offered the directory on was whole.</param>
internal sealed class LiveTreeCheck(
    ILiveTreeInspector inspector,
    RecognisedBuildDirectory directory,
    Func<CancellationToken, LiveTreeQuestion> questions,
    bool planComplete) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass. That table is the preview's,
        // and it is dropped before the question is built, because building it can read the table.
        inspector.Invalidate();

        var query = questions(ct).Ask(directory);
        var findings = inspector.FindLive([query], ct);

        IReadOnlyList<InUseNow> live =
        [
            .. findings.Live
                .Where(tree => tree.Directory.Equals(query.Directory, StringComparison.OrdinalIgnoreCase))
                .Select(tree => new InUseNow(step.Path, string.Join("; ", tree.Holders))),
        ];

        return LiveTreeVeto.AtClean(step, live, findings.Complete, planComplete);
    }
}
