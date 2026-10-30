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
    /// The newest last-write time of <paramref name="directory"/> and of every entry at any depth
    /// below it, or null where the directory is gone or any folder in it could not be listed.
    /// </summary>
    public static DateTime? NewestWrite(string directory, ScanTuner tuning, CancellationToken ct)
    {
        var info = new DirectoryInfo(LongPath.Extended(directory));

        if (!info.Exists)
        {
            // LastWriteTimeUtc answers for a missing path with the Windows epoch, the oldest age there
            // is. See DirectoryAge.
            return null;
        }

        var newest = info.LastWriteTimeUtc.Ticks;
        var refused = 0;

        BoundedFileWalk.Visit(
            LongPath.Extended(directory),
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

        return Volatile.Read(ref refused) == 1 ? null : new DateTime(Interlocked.Read(ref newest), DateTimeKind.Utc);
    }

    private static long Newest(IReadOnlyList<WalkEntry> entries, long newest)
    {
        foreach (var entry in entries)
        {
            newest = Math.Max(newest, entry.LastWriteTimeUtc.Ticks);
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
