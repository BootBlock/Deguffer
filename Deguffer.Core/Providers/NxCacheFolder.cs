using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a folder is one Nx keeps a workspace's task cache in, and may go.
///
/// <para><b>The cache folder goes whole, as Nx's own reset removes it.</b> <c>nx reset --onlyCache</c>
/// is <c>rmSync(cacheDir, { recursive: true, force: true })</c>, and the next Nx run makes the folder
/// again (<c>create_dir_all</c> in <c>NxCache::new</c>). What is inside is Nx's alone: an entry folder
/// per task hash, <c>terminalOutputs</c> holding each task's log, <c>batchOutputs</c> holding batch
/// logs, and the write probes Nx leaves for a moment. Its configuration and its index are beside the
/// folder, never in it, so nothing of the user's is inside.</para>
///
/// <para><b>Recognised by the folder Nx makes in every cache it opens.</b> <c>NxCache::new</c> creates
/// <c>terminalOutputs</c> before anything else, as the TypeScript cache before it did, in every version
/// read from 18 to 24.0.3, so a folder called
/// <c>cache</c> without it was not opened by Nx. A name is not evidence here: <c>cache</c> is the most
/// ordinary folder name there is.</para>
/// </summary>
internal static class NxCacheFolder
{
    /// <summary>The name Nx gives the cache folder, under <c>.nx</c> and under <c>~\.nx\&lt;id&gt;</c>.</summary>
    public const string Name = "cache";

    /// <summary>The folder <c>NxCache::new</c> makes inside every cache it opens.</summary>
    public const string TerminalOutputs = "terminalOutputs";

    /// <summary>
    /// <paramref name="path"/> as an Nx cache whose index is in <paramref name="index"/>, or why it is
    /// not one. Neither where nothing is at <paramref name="path"/>, which is nothing to offer and
    /// nothing to explain. <paramref name="absentIsEmpty"/> is
    /// <see cref="NxCacheIndex.AllowsRemoval"/>'s.
    /// </summary>
    public static NxCacheVerdict Examine(string path, string index, bool absentIsEmpty)
    {
        switch (LongPath.ProbeDirectory(path))
        {
            case PathPresence.Absent:
                return default;

            case PathPresence.Refused:
                return NxCacheVerdict.Declining(path, "Windows would not say what is in it, so it is left alone.");
        }

        if (LongPath.IsReparsePoint(path))
        {
            return NxCacheVerdict.Declining(path, CacheLevelWalk.LinkReason);
        }

        var terminalOutputs = System.IO.Path.Combine(path, TerminalOutputs);

        if (LongPath.ProbeDirectory(terminalOutputs) != PathPresence.Present || LongPath.IsReparsePoint(terminalOutputs))
        {
            return NxCacheVerdict.Declining(
                path,
                $"It does not hold the {TerminalOutputs} folder Nx makes in every cache it opens, so it is not "
                + "known to be Nx's and is left alone.");
        }

        var databases = NxCacheIndex.Read(index);

        if (databases.AllowsRemoval(absentIsEmpty))
        {
            return new NxCacheVerdict(path, null);
        }

        return databases.Reading switch
        {
            NxIndexReading.Absent => NxCacheVerdict.Declining(
                path,
                $"Its index is not in '{index}', so it may be somewhere Deguffer cannot see, kept by a version "
                + "of Nx that would report a removed task as restored and restore nothing. It is left alone. "
                + "Run 'nx reset' in the workspace to clear the cache and its index together."),

            NxIndexReading.Unknown => NxCacheVerdict.Declining(
                path,
                $"Deguffer could not read '{index}' to see which version of Nx keeps its index, so it is left alone."),

            // Older, the one reading left once the gate has refused.
            _ => NxCacheVerdict.Declining(
                path,
                $"'{index}' holds an index written by Nx 23.2 or earlier, which would report a removed task "
                + "as restored from the cache and restore nothing, so it is left alone. Run 'nx reset' in "
                + "the workspace to clear the cache and its index together."),
        };
    }
}

/// <summary>What <see cref="NxCacheFolder.Examine"/> found: the cache, or why it is left alone.</summary>
/// <param name="Cache">The recognised cache folder, which may go, or null.</param>
/// <param name="Declined">The folder left alone and why, or null where there was nothing to decline.</param>
internal readonly record struct NxCacheVerdict(string? Cache, (string Path, string Reason)? Declined)
{
    public static NxCacheVerdict Declining(string path, string reason) => new(null, (path, reason));
}
