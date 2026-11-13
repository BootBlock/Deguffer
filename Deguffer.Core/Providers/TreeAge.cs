using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The newest write anywhere in a tree, from one walk of it.
///
/// <para>Where <see cref="DirectoryAge"/> reads one level, this reads every level, for a rule whose
/// vendor defines a folder's age as the newest file in it at any depth. JetBrains' clean-up of old IDE
/// directories is the case: it walks the whole tree and takes the newest modification time of every
/// directory and file it meets (<c>collectDirectoryData</c> in <c>OldDirectoryCleaner.java</c>).</para>
///
/// <para><b>Honest where the vendor is not.</b> The IDE skips an entry it cannot read and keeps the
/// newest time of what it could, which reads <em>older</em> than the truth, the direction that invites
/// a deletion. Here a listing Windows refuses anywhere in the tree makes the whole answer unknown.
/// Every file's time comes from its own folder's listing, so a file is unreadable only where that
/// listing is.</para>
///
/// <para>A link is counted by its own time and never entered, as the IDE does and as
/// <see cref="BoundedFileWalk"/> holds for every walk: what it points at is somewhere else's, and its
/// removal removes only the link.</para>
/// </summary>
internal static class TreeAge
{
    /// <summary>
    /// The latest FILETIME a <see cref="DateTime"/> can hold. NTFS keeps whatever a program sets, up to
    /// the year 30828, and a write dated past this is newer than any cutoff, so the answer is held here
    /// rather than read as unknown or thrown out of the walk.
    /// </summary>
    private static readonly long LatestFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// The newest last-write time of <paramref name="directory"/> and of every entry at any depth
    /// below it, or null where the directory is gone or any folder in it could not be listed.
    /// </summary>
    public static DateTime? NewestWrite(string directory, ScanTuner tuning, CancellationToken ct)
    {
        var extended = LongPath.Extended(directory);

        // Read as a FILETIME, as the listings below read every entry, so a root dated past the year 9999
        // cannot throw. A directory that is gone or unreadable has no age to give rather than the
        // Windows epoch, the oldest age there is. See DirectoryAge.
        if (FileAttributeRead.Read(extended, out var attributes, out _, out _, out var newest) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            return null;
        }

        var refused = 0;

        BoundedFileWalk.Visit(
            extended,
            rootState: 0,
            tuning,
            FileNames.Stores,
            (_, contents, descend) =>
            {
                if (contents.WasRefused)
                {
                    Volatile.Write(ref refused, 1);
                }

                var local = Newest(contents.Entries, Newest(contents.Links, Newest(contents.ReparseFiles, 0)));
                RaiseTo(ref newest, local);

                foreach (var child in contents.Entries)
                {
                    if (child.IsDirectory)
                    {
                        descend(child, 0);
                    }
                }
            },
            static () => { },
            TimeProvider.System,
            ct);

        return Volatile.Read(ref refused) == 1 ? null : DateTime.FromFileTimeUtc(Math.Min(Interlocked.Read(ref newest), LatestFileTime));
    }

    private static long Newest(ReadOnlySpan<WalkEntry> entries, long newest)
    {
        foreach (var entry in entries)
        {
            newest = Math.Max(newest, entry.LastWriteFileTime);
        }

        return newest;
    }

    /// <summary>Raise <paramref name="target"/> to <paramref name="value"/>, from concurrent listings.</summary>
    private static void RaiseTo(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);

        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);

            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
