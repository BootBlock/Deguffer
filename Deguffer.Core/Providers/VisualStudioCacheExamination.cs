using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One cache found in a <c>.vs</c> folder, with the files that proved it.</summary>
/// <param name="Path">The folder or file a step removes.</param>
/// <param name="Cache">Which cache it is.</param>
/// <param name="Solution">The solution it belongs to, by the name the user knows.</param>
internal sealed record VisualStudioCacheFound(string Path, VisualStudioCache Cache, string Solution);

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
/// §5.2 applied inside one <c>.vs</c> folder, by <see cref="VisualStudioCacheLayout"/>.
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
    public static VisualStudioCacheContents Of(VisualStudioSolutionFolder folder, CancellationToken ct = default)
    {
        var caches = new List<VisualStudioCacheFound>();
        var kept = new List<(string Path, string Reason)>();
        var containers = new List<string>();
        var notes = new List<PlanNote>();
        var unreadable = false;

        Walk(folder.Path, VisualStudioLevel.Folder, folder.Name);

        return new VisualStudioCacheContents(caches, kept, containers, notes, unreadable);

        void Walk(string directory, VisualStudioLevel level, string solution)
        {
            ct.ThrowIfCancellationRequested();
            containers.Add(directory);

            if (FolderEntries.Of(directory) is not { } entries)
            {
                notes.Add(UnreadableRoot.Note(directory));
                unreadable = true;
                return;
            }

            foreach (var entry in entries)
            {
                var path = Path.Combine(directory, entry.Name);
                var kind = entry is DirectoryInfo ? TargetKind.Directory : TargetKind.File;

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    notes.Add(CacheLevelWalk.Note(path));
                    kept.Add((path, CacheLevelWalk.LinkReason));
                }
                else if (VisualStudioCacheLayout.CacheNamed(level, entry.Name, kind) is { } cache)
                {
                    if (cache.Evidence(path))
                    {
                        caches.Add(new VisualStudioCacheFound(path, cache, solution));
                    }
                    else
                    {
                        kept.Add((path, VisualStudioCacheLayout.NotConfirmedReason(cache)));
                    }
                }
                else if (kind == TargetKind.Directory && Inner(level, entry.Name) is { } inner)
                {
                    Walk(path, inner.Level, inner.Solution);
                }
                else
                {
                    kept.Add((path, Reason(level, entry.Name, kind)));
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

    private static string Reason(VisualStudioLevel level, string name, TargetKind kind) => (level, kind) switch
    {
        (VisualStudioLevel.Folder, TargetKind.Directory) when VisualStudioCacheLayout.KeptReason(level, name) == VisualStudioCacheLayout.UnknownReason =>
            VisualStudioCacheLayout.NoSolutionReason,
        (VisualStudioLevel.Solution, TargetKind.Directory) when VisualStudioCacheLayout.IsVersionShaped(name) =>
            VisualStudioCacheLayout.UnknownVersionReason,
        _ => VisualStudioCacheLayout.KeptReason(level, name),
    };
}
