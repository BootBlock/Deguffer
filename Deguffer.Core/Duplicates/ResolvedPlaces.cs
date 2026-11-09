using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Places named by a setting or a provider, each with what to say of a copy in it, asked whether
/// they hold a copy's path (§7.4: a copy in the temporary folder, where a Storage clean deletes, or
/// in a cloud folder is not one a group can count on keeping).
///
/// <para><b>Asked at every path each place is reachable at.</b> A copy's path is the final path
/// Windows gives, with every link followed, while a place is named as its setting names it: through
/// a substituted drive, a volume mounted in a folder, or a junction on the way. Each place is
/// followed to its final path as well as through <see cref="ReachedFolder"/>, so a copy in the npm
/// cache reached through a junction is still in the npm cache (<see cref="ReachedFolder.FollowedTo"/>, which program
/// folders are followed by too). A place Windows will not open keeps
/// the paths its name reaches, and is never dropped.</para>
///
/// <para><b>Ignoring case</b>, which can only find a copy in a place more often, and so keep fewer.</para>
/// </summary>
internal sealed class ResolvedPlaces
{
    private readonly FolderIndex<(int Position, CleanedPlace Place, string Start, string What)> _byStart;

    private ResolvedPlaces(FolderIndex<(int Position, CleanedPlace Place, string Start, string What)> byStart) => _byStart = byStart;

    /// <param name="places">Each place, with what to say of a copy in it.</param>
    public static ResolvedPlaces Resolve(
        IEnumerable<(CleanedPlace Place, string What)> places, IVolumeInventory volumes, FileInformation files)
    {
        List<(int Position, CleanedPlace Place, string Start, string What)> resolved = [];
        var position = 0;

        foreach (var (place, what) in places)
        {
            if (LongPath.Configured(place.Path) is not { } configured)
            {
                continue;
            }

            HashSet<string> starts = new(ReachedFolder.At(configured, volumes).Places, StringComparer.OrdinalIgnoreCase);

            if (ReachedFolder.FollowedTo(configured, volumes, files.FinalPath) is { } final)
            {
                starts.UnionWith(final.Places);
            }

            // Made comparable once here, rather than at each question, because that asks the file
            // system and a search asks about every copy of every group.
            foreach (var start in starts.Select(ReachedFolder.Comparable))
            {
                resolved.Add((position, place, start, what));
            }

            position++;
        }

        return new ResolvedPlaces(new FolderIndex<(int Position, CleanedPlace Place, string Start, string What)>(resolved, entry => [entry.Start]));
    }

    /// <summary>What the first place holding <paramref name="path"/> says of it, or null where none holds it.</summary>
    /// <param name="path">A full path, in display form.</param>
    public string? WhatHolds(string path) => WhatHoldsComparable(ReachedFolder.Comparable(path));

    /// <summary>What the first place holding <paramref name="comparable"/> says of it, or null where none holds it.</summary>
    /// <param name="comparable">A full path in <see cref="ReachedFolder.Comparable"/> form.</param>
    public string? WhatHoldsComparable(string comparable)
    {
        // The first of the places, which the walk up from the deepest folder does not come to first.
        string? first = null;
        var earliest = int.MaxValue;

        foreach (var entries in _byStart.Holding(comparable))
        {
            foreach (var (position, place, start, what) in entries)
            {
                if (position < earliest && place.HoldsComparable(start, comparable))
                {
                    first = what;
                    earliest = position;
                }
            }
        }

        return first;
    }
}
