using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The Outlook mail stores inside a folder, looked for on the disk immediately before a removal that
/// cannot leave one behind: a tool's own command, a Recycle Bin emptied by Windows or removed whole by
/// Deguffer, and Explore moving a folder to the Recycle Bin (§9).
///
/// <para><b>Asked again rather than trusted from the plan</b>, for the reason the guard on recently
/// changed files is asked again inside <see cref="FileRemover"/>: a plan is made minutes before it
/// runs, and a store saved into a cache folder while the preview sat on screen is exactly what those
/// minutes can bring. A removal Deguffer performs file by file needs no such look, because its own walk
/// meets every store as it goes and steps over it. A removal whose subject goes whole cannot step over
/// one, which is why it is on the list above (see <see cref="DeleteDirectoryStep.IsIndivisible"/>).</para>
///
/// <para><b>What it costs is a walk of the folder, and it is paid only where it has to be.</b> The
/// removals above are the ones whose reach nothing else looks inside at the moment they run. The walk
/// stops at nothing it does not need: it reads names alone, and it never opens a file.</para>
///
/// <para>The folder asked about is listed as it stands, link or not, because that is what the removal
/// it stands in front of will act on. No folder below it is followed through a link, for the reason the
/// removal walk gives: what is on the far side belongs to wherever the link points. A file is asked
/// about whatever mark it carries, as the removal walk asks it. A folder Windows will not list is
/// skipped (§5.3), which is what the measurement that made the plan did too.</para>
/// </summary>
public static class MailStoreSearch
{
    /// <returns>Every store found, in display form, in the order the walk met them.</returns>
    public static IReadOnlyList<string> Under(string path, IFileSystem fs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fs);

        var found = new List<string>();

        // An explicit stack rather than recursion, because the folders this is asked about are the
        // deepest trees on a developer's disk.
        var pending = new Stack<string>();
        pending.Push(LongPath.Extended(path));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<FileSystemEntry> entries;
            try
            {
                entries = fs.EnumerateEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // A folder that is not there holds nothing, and one Windows will not list is skipped
                // as the measurement behind the plan skipped it.
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!entry.IsReparsePoint)
                    {
                        pending.Push(entry.FullName);
                    }
                }

                // Whatever mark it carries: a OneDrive placeholder and a deduplicated file carry the mark
                // Windows puts on a link, and every removal this look stands in front of would take one.
                else if (MailStore.Is(entry.FullName))
                {
                    found.Add(LongPath.Display(entry.FullName));
                }
            }
        }

        return found;
    }
}
