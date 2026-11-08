using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one Berry global folder's cache is, read from the disk.</summary>
internal enum YarnBerryCacheState
{
    /// <summary>There is no cache folder.</summary>
    Absent,

    /// <summary>Windows would not say what is there, or would not list all of it.</summary>
    Refused,

    /// <summary>The global folder or its cache is a link to somewhere else.</summary>
    Link,

    /// <summary>The cache holds an entry Yarn does not write there.</summary>
    Stranger,

    /// <summary>The cache is there and holds nothing.</summary>
    Empty,

    /// <summary>Every entry in the cache is a file Yarn writes there.</summary>
    Recognised,
}

/// <summary>
/// One Berry global folder as the disk has it: the state of its cache, and the entries beside the
/// cache, which no rule here recognises and every plan names as survivors (§5.2, §5.6).
///
/// <para><b>Recognised means every entry, because the cache goes whole.</b> Berry's own clean deletes
/// the whole folder, and so does this row. One entry Yarn does not write there, such as a folder, a
/// link or a file under another name, stops it, and the cache stays as it is.</para>
/// </summary>
/// <param name="GlobalFolder">The global folder, in display form.</param>
/// <param name="Cache">Its <c>cache</c> folder, in display form.</param>
/// <param name="State">What the cache is.</param>
/// <param name="Cause">
/// What stopped the cache: a name for <see cref="YarnBerryCacheState.Stranger"/>, the path that is a
/// link for <see cref="YarnBerryCacheState.Link"/>, or the path Windows would not list or describe
/// for <see cref="YarnBerryCacheState.Refused"/>. Null otherwise.
/// </param>
/// <param name="Siblings">Every other entry in the global folder, in display form.</param>
internal sealed record YarnBerryFolder(
    string GlobalFolder,
    string Cache,
    YarnBerryCacheState State,
    string? Cause,
    IReadOnlyList<string> Siblings)
{
    /// <summary>
    /// Read <paramref name="globalFolder"/>. The cache is listed until its first unrecognised entry,
    /// and no further, because one is enough to leave it alone and a cache holds thousands of archives.
    /// </summary>
    public static YarnBerryFolder Read(string globalFolder)
    {
        var cache = YarnBerryCache.CacheIn(globalFolder);

        // Both arrive by name, so nothing has classified either. A junction hands back the far side's
        // ordinary entries, and every survivor named here would resolve through the same link and pass.
        if (LongPath.IsReparsePoint(globalFolder))
        {
            return new(globalFolder, cache, YarnBerryCacheState.Link, globalFolder, []);
        }

        // A global folder that will not be listed hides what is beside the cache, and §5.6 could then
        // assert none of it. That is not the same as nothing being there, so the cache is left.
        if (FolderEntries.Of(globalFolder) is not { } entries)
        {
            return new(globalFolder, cache, YarnBerryCacheState.Refused, globalFolder, []);
        }

        var siblings = entries
            .Where(entry => !entry.Name.Equals("cache", StringComparison.OrdinalIgnoreCase))
            .Select(entry => LongPath.Display(entry.FullName))
            .ToList();

        YarnBerryFolder Found(YarnBerryCacheState state, string? cause = null) =>
            new(globalFolder, cache, state, cause, siblings);

        switch (LongPath.ProbeDirectory(cache, out var cacheIsLink))
        {
            case PathPresence.Absent:
                return Found(YarnBerryCacheState.Absent);
            case PathPresence.Refused:
                return Found(YarnBerryCacheState.Refused, cache);
        }

        if (cacheIsLink is true)
        {
            return Found(YarnBerryCacheState.Link, cache);
        }

        using var listing = DirectoryListing.Of(cache);
        var any = false;

        while (listing.MoveNext())
        {
            var entry = listing.Current;

            if (entry is not FileInfo
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !YarnBerryCache.IsCacheFile(entry.Name))
            {
                return Found(YarnBerryCacheState.Stranger, entry.Name);
            }

            any = true;
        }

        return listing.Outcome is not PathPresence.Present ? Found(YarnBerryCacheState.Refused, cache)
            : any ? Found(YarnBerryCacheState.Recognised)
            : Found(YarnBerryCacheState.Empty);
    }
}
