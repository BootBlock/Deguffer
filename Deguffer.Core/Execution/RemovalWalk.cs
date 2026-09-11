using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>What a removal of one tree would act on, gathered before anything is deleted.</summary>
/// <param name="Directories">Every directory the walk reached, the root first.</param>
/// <param name="Files">Every file the guard and the bounds leave to the removal, with its length.</param>
/// <param name="Links">Junctions and symbolic links, which are removed as links and never followed.</param>
/// <param name="Kept">Files and links the guard on recently changed files held back.</param>
/// <param name="Spared">Entries the bounds held back, which the walk did not descend into.</param>
internal sealed record RemovalInventory(
    IReadOnlyList<string> Directories,
    IReadOnlyList<(string Path, long Length)> Files,
    IReadOnlyList<FileSystemEntry> Links,
    int Kept,
    int Spared);

/// <summary>
/// The walk a removal makes before it deletes anything, shared with the check that asks what Windows
/// would still refuse.
///
/// <para><b>One walk for both, because the check has to measure what the removal would
/// attempt.</b> The same guard, the same spared entries and the same refusal to follow a link decide
/// both. Written twice, the rules would drift, and the check would then take bytes out of an
/// estimate that the removal never meant to take — or leave in bytes it will.</para>
///
/// <para>Both exclusions are applied here rather than in the deletion pass because this is where the
/// evidence already is: the enumeration that classified the entry read its timestamp and its path,
/// and gathering is also where a file the removal must not touch stops being a candidate at all. An
/// entry filtered out here is never handed to a delete, so there is no second place holding the same
/// rule.</para>
/// </summary>
internal static class RemovalWalk
{
    /// <param name="extendedRoot">The directory to walk, in the extended-length form §6.3 requires.</param>
    public static RemovalInventory Gather(
        string extendedRoot,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        CancellationToken ct)
    {
        var directories = new List<string>();
        var files = new List<(string, long)>();
        var links = new List<FileSystemEntry>();

        var (kept, spared) = Visit(extendedRoot, keep, bounds, directories, files, links, fs, ct);

        return new RemovalInventory(directories, files, links, kept, spared);
    }

    private static (int Kept, int Spared) Visit(
        string extendedDirectory,
        MinimumAge keep,
        RemovalBounds bounds,
        List<string> directories,
        List<(string, long)> files,
        List<FileSystemEntry> links,
        IFileSystem fs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        directories.Add(extendedDirectory);

        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = fs.EnumerateEntries(extendedDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Unreadable directory: nothing to gather, and §5.3 says skip rather than fail.
            return (0, 0);
        }

        var kept = 0;
        var spared = 0;

        foreach (var entry in entries)
        {
            // Asked before the link branch below, because a spared entry is spared whatever it
            // turns out to be. A scratch directory a running program was handed as a junction is
            // still that program's, and removing the link is still taking it away.
            if (bounds.SparedPaths.Contains(entry.FullName))
            {
                spared++;
                continue;
            }

            if (entry.IsReparsePoint)
            {
                // The guard applies to a link exactly as it applies to a file.
                //
                // A link carries its own timestamps, so a junction made a minute ago is a minute
                // old whatever it points at. Without this, a plan promising "nothing touched in the
                // last seven days is offered" removed one anyway — silently, because a link's
                // length is zero and every scanner skips it, so no figure moved and neither count
                // did either.
                if (keep.Protects(entry.NewestFileTime))
                {
                    kept++;
                    continue;
                }

                // Never followed: deletion would escape the target tree. The removal takes the link
                // itself and stops there.
                links.Add(entry);
                continue;
            }

            if (entry.IsDirectory)
            {
                var below = Visit(entry.FullName, keep, bounds, directories, files, links, fs, ct);
                kept += below.Kept;
                spared += below.Spared;
            }
            else if (keep.Protects(entry.NewestFileTime))
            {
                kept++;
            }
            else
            {
                files.Add((entry.FullName, entry.Length));
            }
        }

        return (kept, spared);
    }
}
