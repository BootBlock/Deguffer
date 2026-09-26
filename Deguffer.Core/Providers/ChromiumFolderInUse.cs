using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a running program is using a Chromium user-data folder, asked by the plan and asked again
/// immediately before each cache inside the folder is removed (§5.3).
///
/// <para><b>What sees it is the command line.</b> A WebView2 host's engine runs from the runtime's
/// own install folder and names the folder it is using as <c>--user-data-dir=</c>, so
/// <see cref="ILiveTreeInspector.FindLiveChildren"/>, asked about the folder's parent, is the one
/// signal that finds it. Microsoft documents that a WebView2 user-data folder cannot be removed while
/// its browser process or any of its child processes is running. A program running from or working
/// in the folder counts as well, by the same method's rules.</para>
///
/// <para>Asked of every Chromium folder, not only WebView2's, because the evidence is the same kind
/// for all of them and every signal is positive. A browser started without the switch, or an
/// application running from its own install folder, is not seen, and its caches are offered as they
/// were before, under the running-process warning.</para>
///
/// <para>A check that could not read every program's command line lets the step run, because the plan
/// offered it on the same partial answer and said so in a note.</para>
/// </summary>
internal sealed class ChromiumFolderInUse(ILiveTreeInspector inspector, string userData) : IUseCheck
{
    /// <summary>Which of <paramref name="folders"/> a running program is using.</summary>
    public static LiveTreeFindings Find(
        ILiveTreeInspector inspector,
        IReadOnlyList<string> folders,
        CancellationToken ct)
    {
        if (folders.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        var parents = folders
            .Select(folder => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var named = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var children = inspector.FindLiveChildren(parents, ct);

        return children with { Live = [.. children.Live.Where(live => named.Contains(live.Directory))] };
    }

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass. That table is the preview's.
        inspector.Invalidate();

        return
        [
            .. Find(inspector, [userData], ct).Live
                .Select(live => new InUseNow(step.Path, string.Join("; ", live.Holders))),
        ];
    }
}
