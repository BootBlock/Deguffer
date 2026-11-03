using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One of restic's cache folders as it stands: the repository caches in it, and everything beside them
/// that is not one.
///
/// <para>A repository's cache is named by a 64-digit identifier, so a <see cref="DisposableChildSet"/>
/// cannot list it, and the folder is classified by <see cref="ResticCacheLayout.IsRepositoryCache"/>
/// instead. Anything else in it, the <c>CACHEDIR.TAG</c> and a <c>restic check</c> cache included, is
/// Tier 4, and a link is declined rather than followed.</para>
/// </summary>
/// <param name="Root">The cache folder, in display form.</param>
/// <param name="Caches">Each repository's cache in it, in display form.</param>
/// <param name="Declined">The folder itself, where it is a link, or each link in it.</param>
/// <param name="Unrecognised">Everything else in it, with why it stays.</param>
/// <param name="Notes">What the user is told about the folder.</param>
/// <param name="Unreadable">Whether Windows would not describe or list the folder.</param>
internal sealed record ResticCacheFolder(
    string Root,
    IReadOnlyList<string> Caches,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<(string Path, string Reason)> Unrecognised,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable)
{
    private const string TagFile = "CACHEDIR.TAG";

    private const string TagReason =
        "The tag restic writes to mark this folder as a cache, which backup programs read so that they leave it out.";

    private const string CheckCacheReason =
        "A cache 'restic check' made for itself. A check that is running uses it, so Deguffer does not offer it.";

    private const string StrangerReason = "Not something restic writes in its cache folder, so it is left alone.";

    /// <summary>The folder at <paramref name="root"/>, or null where there is no folder there.</summary>
    public static ResticCacheFolder? Read(string root)
    {
        switch (LongPath.ProbeDirectory(root))
        {
            case PathPresence.Absent:
                return null;

            case PathPresence.Refused:
                return new ResticCacheFolder(root, [], [], [], [UnreadableRoot.UnreachedNote(root)], Unreadable: true);
        }

        // The folder arrives by name, from a variable or a default, so nothing has classified it, and a
        // repository's cache on the far side of a link would be cleared through it.
        if (LongPath.IsReparsePoint(root))
        {
            return new ResticCacheFolder(root, [], [(root, CacheLevelWalk.LinkReason)], [], [CacheLevelWalk.Note(root)], Unreadable: false);
        }

        if (FolderEntries.Of(root) is not { } entries)
        {
            return new ResticCacheFolder(root, [], [], [], [UnreadableRoot.Note(root)], Unreadable: true);
        }

        var caches = new List<string>();
        var declined = new List<(string Path, string Reason)>();
        var unrecognised = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();

        foreach (var entry in entries)
        {
            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                declined.Add((path, CacheLevelWalk.LinkReason));
                notes.Add(CacheLevelWalk.Note(path));
            }
            else if (entry is DirectoryInfo && ResticCacheLayout.IsRepositoryCache(entry.Name))
            {
                caches.Add(path);
            }
            else if (entry.Name.Equals(TagFile, StringComparison.OrdinalIgnoreCase))
            {
                unrecognised.Add((path, TagReason));
            }
            else
            {
                var survivor = (path, ResticCacheLayout.IsTakenByCleanup(entry.Name) ? CheckCacheReason : StrangerReason);
                unrecognised.Add(survivor);
                notes.Add(CacheLevelWalk.SparedNote(survivor));
            }
        }

        return new ResticCacheFolder(root, caches, declined, unrecognised, notes, Unreadable: false);
    }
}
