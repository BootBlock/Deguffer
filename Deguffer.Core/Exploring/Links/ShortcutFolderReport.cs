using System.IO.Enumeration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// The shortcuts and links in the Start menu, on the desktop and in the Startup folders whose target
/// is gone, read from those folders rather than from a scan.
///
/// <para>Read without a scan because those folders are rarely the place on screen: the Files layout's
/// own list is of the folder a scan reached, and a removed program's shortcuts are where Windows
/// keeps shortcuts.</para>
/// </summary>
/// <param name="Links">The links whose target is gone, by path. Each is offered for removal.</param>
/// <param name="Unreachable">
/// How many lead to a target Deguffer cannot reach, which are never offered and so never listed.
/// </param>
/// <param name="Unread">
/// Every folder, the named ones or one inside them, that Windows would not list, so a short report
/// says it is short rather than reading as a clean Start menu. A folder that is not there is not one:
/// it holds nothing to report.
/// </param>
public sealed record ShortcutFolderReport(IReadOnlyList<BrokenLink> Links, int Unreachable, IReadOnlyList<string> Unread)
{
    /// <summary>
    /// Read every folder of <paramref name="folders"/> and everything inside them, without following a
    /// link into the place it leads, and ask about every link and shortcut found there.
    /// </summary>
    /// <param name="folders">From <see cref="ShortcutFolders.Of"/>.</param>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    public static ShortcutFolderReport Read(IReadOnlyList<string> folders, LinkInspection inspection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(inspection);

        List<BrokenLink> gone = [];
        List<string> unread = [];
        var unreachable = 0;
        var waiting = new Stack<string>(folders.Reverse());

        while (waiting.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();

            if (FolderEntries.Of(folder, Listed) is not { } entries)
            {
                unread.Add(folder);
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory && !entry.IsLink)
                {
                    waiting.Push(entry.Path);
                    continue;
                }

                if (!entry.IsLink && !WindowsLinkTargetReader.IsShortcutName(entry.Path))
                {
                    continue;
                }

                switch (inspection.Inspect(entry.Path, entry.IsLink, entry.IsDirectory, entry.Length))
                {
                    case { IsOffered: true } link:
                        gone.Add(link);
                        break;

                    case not null:
                        unreachable++;
                        break;
                }
            }
        }

        gone.Sort(static (one, other) => StringComparer.OrdinalIgnoreCase.Compare(one.Path, other.Path));

        return new ShortcutFolderReport(gone, unreachable, unread);
    }

    /// <summary>What one entry is listed as: no more than this keeps of it.</summary>
    private readonly record struct ListedEntry(string Path, bool IsDirectory, bool IsLink, long Length);

    private static ListedEntry Listed(ref FileSystemEntry entry) => new(
        LongPath.Display(entry.ToFullPath()),
        entry.IsDirectory,
        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
        entry.IsDirectory ? 0 : entry.Length);
}
