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
///
/// <para>The directory asked about need not be the step's. A superseded Squirrel build is held by the
/// application running from the build beside it, so the veto asks about the installation, and what it
/// finds holds back the step that removes the build.</para>
/// </summary>
/// <param name="directory">The directory the veto asked about, and the project it belongs to.</param>
/// <param name="lockFilesOf">
/// The veto's own rule for the files to ask about, applied again rather than its answer remembered.
/// Unreal's logs are found by listing the project's <c>Saved\Logs</c>, and an editor opened on the
/// project after the preview may have written the first one there.
/// </param>
internal sealed class LiveTreeCheck(
    ILiveTreeInspector inspector,
    RecognisedBuildDirectory directory,
    Func<RecognisedBuildDirectory, IReadOnlyList<string>> lockFilesOf) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass. That table is the preview's.
        inspector.Invalidate();

        var query = new LiveTreeQuery(directory.Path, directory.Project, lockFilesOf(directory));

        return
        [
            .. inspector.FindLive([query], ct).Live
                .Where(live => live.Directory.Equals(query.Directory, StringComparison.OrdinalIgnoreCase))
                .Select(live => new InUseNow(step.Path, string.Join("; ", live.Holders))),
        ];
    }
}
