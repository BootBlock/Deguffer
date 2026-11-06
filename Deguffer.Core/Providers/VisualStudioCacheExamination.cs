using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>One cache found in a <c>.vs</c> folder, with the files that proved it.</summary>
/// <param name="Path">The folder or file a step removes.</param>
/// <param name="Cache">Which cache it is.</param>
/// <param name="Solution">The solution it belongs to, by the name the user knows.</param>
/// <param name="Age">
/// When it was last written, by <see cref="DirectoryAge"/>'s rule for a folder and its own last write
/// for a file, read from the listings that proved it, or null where those listings could not date it.
/// </param>
internal sealed record VisualStudioCacheFound(string Path, VisualStudioCache Cache, string Solution, DateTime? Age);

/// <summary>What one <c>.vs</c> folder came to.</summary>
/// <param name="Caches">The caches confirmed, ready to be measured.</param>
/// <param name="Kept">
/// Every other entry at a level the walk read, with why it stays. They are what §5.6 asserts survived:
/// each is a sibling of a cache under the same parent, which is exactly when an over-broad rule takes
/// both.
/// </param>
/// <param name="Containers">
/// The <c>.vs</c> folder and every solution and version folder the walk read. Each stays standing
/// while caches inside it go, so §5.6 asserts it survived: a rule reaching one level too far takes
/// the container with the cache, and nothing else would be missing to say so.
/// </param>
/// <param name="Notes">A link that was not followed, or a level that could not be listed.</param>
/// <param name="Unreadable">Whether Windows kept a level's contents out of the walk.</param>
internal sealed record VisualStudioCacheContents(
    IReadOnlyList<VisualStudioCacheFound> Caches,
    IReadOnlyList<(string Path, string Reason)> Kept,
    IReadOnlyList<string> Containers,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable);

/// <summary>
/// §5.2 applied inside one <c>.vs</c> folder, by <see cref="VisualStudioCacheLayout"/>, from the one
/// <see cref="VisualStudioCacheReader"/> reading the live-tree check was asked from, so nothing in it
/// is listed again.
///
/// <para><see cref="CacheLevelWalk"/> is not used, for two reasons it cannot meet without changing
/// what every other provider's table means. Two of the caches are files, and that walk reads
/// directories alone, so a solution option file beside them would be neither offered nor asserted.
/// And the levels have no fixed names: the solution folders are named by the solution files beside
/// <c>.vs</c>, and the version folders by whichever Visual Studio opened them. The safety facts it
/// carries are kept here as they are there: a link is declined and never followed, a level that
/// cannot be listed is said, and every entry left alone is asserted to survive.</para>
/// </summary>
internal static class VisualStudioCacheExamination
{
    public static VisualStudioCacheContents Of(VisualStudioSolutionFolder folder, VisualStudioListing reading, CancellationToken ct = default)
    {
        var caches = new List<VisualStudioCacheFound>();
        var kept = new List<(string Path, string Reason)>();
        var containers = new List<string>();
        var notes = new List<PlanNote>();
        var unreadable = false;

        Walk(reading, VisualStudioLevel.Folder, folder.Name);

        return new VisualStudioCacheContents(caches, kept, containers, notes, unreadable);

        void Walk(VisualStudioListing listing, VisualStudioLevel level, string solution)
        {
            ct.ThrowIfCancellationRequested();
            containers.Add(listing.Path);

            if (listing.Entries is not { } entries)
            {
                notes.Add(UnreadableRoot.Note(listing.Path));
                unreadable = true;
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.IsLink)
                {
                    notes.Add(CacheLevelWalk.Note(entry.Path));
                    kept.Add((entry.Path, CacheLevelWalk.LinkReason));
                }
                else if (VisualStudioCacheLayout.CacheNamed(level, entry.Name, entry.Kind) is { } cache)
                {
                    if (cache.Evidence(listing, entry))
                    {
                        caches.Add(new VisualStudioCacheFound(entry.Path, cache, solution, AgeOf(listing, entry)));
                    }
                    else
                    {
                        kept.Add((entry.Path, VisualStudioCacheLayout.NotConfirmedReason(cache)));
                    }
                }
                else if (entry.Kind == TargetKind.Directory && Inner(level, entry.Name) is { } inner)
                {
                    Walk(listing.Inside(entry), inner.Level, inner.Solution);
                }
                else
                {
                    kept.Add((entry.Path, Reason(level, entry.Name, entry.Kind)));
                }
            }

            (VisualStudioLevel Level, string Solution)? Inner(VisualStudioLevel level, string name) => level switch
            {
                VisualStudioLevel.Folder when folder.SolutionNamed(name) is { } named => (VisualStudioLevel.Solution, named),
                VisualStudioLevel.Solution when VisualStudioCacheLayout.IsKnownVersion(name) => (VisualStudioLevel.Version, solution),
                _ => null,
            };
        }
    }

    /// <summary>
    /// A file's own time, and a folder's by <see cref="DirectoryAge"/>'s rule from the listings
    /// that proved it: its own time from the folder holding it, and its entries' from its own.
    /// Every folder rule declines a folder it could not list, so a confirmed one has its entries, and
    /// one without them has no age, as <see cref="DirectoryAge"/> answers for a refusal.
    /// </summary>
    private static DateTime? AgeOf(VisualStudioListing folder, VisualStudioEntry cache) =>
        cache.Kind == TargetKind.File
            ? OneLevelAge.ToUtc(cache.NewestFileTime)
            : folder.Inside(cache).Entries is { } entries
                ? DirectoryAge.Of(cache.NewestFileTime, entries.Select(entry => entry.NewestFileTime))
                : null;

    private static string Reason(VisualStudioLevel level, string name, TargetKind kind) => (level, kind) switch
    {
        (VisualStudioLevel.Folder, TargetKind.Directory) when VisualStudioCacheLayout.KeptReason(level, name) == VisualStudioCacheLayout.UnknownReason =>
            VisualStudioCacheLayout.NoSolutionReason,
        (VisualStudioLevel.Solution, TargetKind.Directory) when VisualStudioCacheLayout.IsVersionShaped(name) =>
            VisualStudioCacheLayout.UnknownVersionReason,
        _ => VisualStudioCacheLayout.KeptReason(level, name),
    };
}
