using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a superseded IDE version was started between the preview and the clean, asked of the one
/// file the IDE writes at every start.
///
/// <para>The plan offers a system directory only where nothing in it was written for
/// <paramref name="shelfLife"/>. Starting that version again rewrites <c>.home</c> before anything
/// else (<c>runPostAppInitTasks</c> in <c>ApplicationLoader.kt</c>), so a <c>.home</c> written inside
/// the shelf life now means the version is in use, and its directory is held back. One file is read,
/// not the tree, because the tree's age was the plan's question and this is only whether the answer
/// has been overturned since.</para>
/// </summary>
internal sealed class JetBrainsLaunchCheck(TimeProvider time, TimeSpan shelfLife) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // Each step is a derived folder inside the version's folder, which is where .home is.
        var home = Path.Combine(Path.GetDirectoryName(step.Path)!, JetBrainsSystemDirectory.HomeFileName);

        switch (LongPath.ProbeFile(home))
        {
            case PathPresence.Absent:
                // Nothing has started it: a start writes this file first.
                return [];
            case PathPresence.Refused:
                return [new InUseNow(step.Path, "Windows would not say whether this version was started since the preview")];
        }

        var written = new FileInfo(LongPath.Extended(home)).LastWriteTimeUtc;

        return written > time.GetUtcNow().UtcDateTime - shelfLife
            ? [new InUseNow(step.Path, "this version was started since the preview")]
            : [];
    }
}
