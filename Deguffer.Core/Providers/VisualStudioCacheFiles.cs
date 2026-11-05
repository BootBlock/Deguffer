using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every file Visual Studio may hold open in the caches of one <c>.vs</c> folder, for the live-tree
/// check to ask Windows about, taken from one <see cref="VisualStudioCacheReader"/> reading.
///
/// <para><b>By name and level alone, never by the evidence</b>, for the reason the reading is: every
/// file below a folder at a cache's name and level, and every file at a file cache's, with each
/// SQLite companion the same listing found beside a database.</para>
///
/// <para><b>Every file named was listed</b>, so the query says so and the inspector does not ask
/// Windows about each one again (<see cref="LiveTreeQuery.LockFilesListed"/>). A companion is named
/// only where the listing found it for that reason: SQLite holds the database itself open while any
/// companion is in use, so the database's own name is the question.</para>
///
/// <para>A folder Windows will not list adds nothing. Removing it needs the same listing, so the step
/// fails rather than deleting anything nobody asked about.</para>
/// </summary>
internal static class VisualStudioCacheFiles
{
    /// <summary>The live-tree query for <paramref name="candidate"/>, from <paramref name="reading"/> of its <c>.vs</c> folder.</summary>
    public static LiveTreeQuery Query(RecognisedBuildDirectory candidate, VisualStudioListing reading) =>
        new(candidate.Path, candidate.Project, Of(reading)) { LockFilesListed = true };

    public static IReadOnlyList<string> Of(VisualStudioListing reading)
    {
        var files = new List<string>();

        Level(reading, VisualStudioLevel.Folder);

        return files;

        void Level(VisualStudioListing listing, VisualStudioLevel level)
        {
            foreach (var entry in listing.Entries ?? [])
            {
                if (entry.IsLink)
                {
                    continue;
                }

                if (VisualStudioCacheLayout.CacheNamed(level, entry.Name, entry.Kind) is not null)
                {
                    if (entry.Kind == TargetKind.File)
                    {
                        files.Add(entry.Path);
                        files.AddRange(
                            SqliteDatabaseSignature.CompanionNamesOf(entry.Name)
                                .Where(listing.Holds)
                                .Select(companion => Path.Combine(listing.Path, companion)));
                    }
                    else
                    {
                        Whole(listing.Inside(entry));
                    }
                }
                else if (entry.Kind == TargetKind.Directory && level != VisualStudioLevel.Version)
                {
                    Level(listing.Inside(entry), level + 1);
                }
            }
        }

        void Whole(VisualStudioListing listing)
        {
            foreach (var entry in listing.Entries ?? [])
            {
                if (entry.IsLink)
                {
                    continue;
                }

                if (entry.Kind == TargetKind.Directory)
                {
                    Whole(listing.Inside(entry));
                }
                else
                {
                    files.Add(entry.Path);
                }
            }
        }
    }
}
