using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// The walk, through the same <see cref="BoundedFileWalk"/> the scanners use, doing as little as a
/// caller can with what it finds.
///
/// <para>What a scanner does with each entry costs little beside listing it, and differs between
/// the scanners. Timing the bare walk keeps a change to the walk from being hidden by one of them.
/// </para>
/// </summary>
internal static class WalkRoute
{
    public static RunTally Run(string folder, CancellationToken ct)
    {
        long entries = 0;
        var refused = 0;

        // §6.3: the walk is handed the extended form, as both scanners hand it, so a tree deeper
        // than MAX_PATH is walked rather than cut short and timed as though it were smaller.
        BoundedFileWalk.Visit<byte>(
            LongPath.Extended(folder),
            rootState: 0,
            (_, contents, descend) =>
            {
                if (contents.WasRefused)
                {
                    Interlocked.Exchange(ref refused, 1);
                }

                foreach (var entry in contents.Entries)
                {
                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, 0);
                    }
                }

                Interlocked.Add(ref entries, contents.Entries.Count + contents.Links.Count + contents.ReparseFiles.Count);
            },
            onLevel: static () => { },
            ct);

        return new RunTally(Interlocked.Read(ref entries), BytesRead: 0, Complete: Volatile.Read(ref refused) == 0);
    }
}
