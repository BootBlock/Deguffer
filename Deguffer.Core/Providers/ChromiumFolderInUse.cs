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
/// <para>An answer that could not read every program's command line offers none of the folder's
/// caches, and holds back a step at the clean. See <see cref="LiveTreeVeto.AtClean"/>.</para>
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
            .Select(ParentOf)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var named = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var children = inspector.FindLiveChildren(parents, ct);
        IReadOnlyList<LiveTree> live = [.. children.Live.Where(live => named.Contains(live.Directory))];

        // A parent left unanswered leaves every folder below it unanswered, and no other.
        return children.Complete || children.Unanswered.Count == 0
            ? children with { Live = live }
            : LiveTreeFindings.Partly(live, [.. folders.Where(folder => ParentOf(folder) is { } parent && !children.IsAnswered(parent))]);
    }

    private static string? ParentOf(string folder) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one process table for a planning pass, and the preview's may be past
        // the limit.
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        var findings = Find(inspector, [userData], ct);

        IReadOnlyList<InUseNow> inUse =
        [
            .. findings.Live.Select(live => new InUseNow(step.Path, string.Join("; ", live.Holders))),
        ];

        return LiveTreeVeto.AtClean(step, inUse, findings.Complete);
    }
}
