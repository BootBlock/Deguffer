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
/// cache reached through a junction is still in the npm cache (<see cref="FollowedTo"/>, which program
/// folders are followed by too). A place Windows will not open keeps
/// the paths its name reaches, and is never dropped.</para>
///
/// <para><b>Ignoring case</b>, which can only find a copy in a place more often, and so keep fewer.</para>
/// </summary>
internal sealed class ResolvedPlaces
{
    private readonly IReadOnlyList<(CleanedPlace Place, IReadOnlyList<string> Starts, string What)> _places;

    private ResolvedPlaces(IReadOnlyList<(CleanedPlace, IReadOnlyList<string>, string)> places) => _places = places;

    /// <param name="places">Each place, with what to say of a copy in it.</param>
    public static ResolvedPlaces Resolve(
        IEnumerable<(CleanedPlace Place, string What)> places, IVolumeInventory volumes, FileInformation files)
    {
        List<(CleanedPlace, IReadOnlyList<string>, string)> resolved = [];

        foreach (var (place, what) in places)
        {
            if (LongPath.Configured(place.Path) is not { } configured)
            {
                continue;
            }

            HashSet<string> starts = new(ReachedFolder.At(configured, volumes).Places, StringComparer.OrdinalIgnoreCase);

            if (FollowedTo(configured, volumes, files) is { } final)
            {
                starts.UnionWith(final.Places);
            }

            // Made comparable once here, rather than at each question, because that asks the file
            // system and a search asks about every copy of every group.
            resolved.Add((place, [.. starts.Select(ReachedFolder.Comparable)], what));
        }

        return new ResolvedPlaces(resolved);
    }

    /// <summary>
    /// The folder <paramref name="path"/> leads to once every link on the way to it is followed, which
    /// is the path a copy found there carries, or null where Windows would not open it.
    ///
    /// <para>A share, a network drive and a drive that keeps its files in the cloud are never opened,
    /// as a search location is not (<see cref="SearchLocations"/>): opening is itself a conversation
    /// with another machine or the cloud client, and no copy is searched there to be matched. A path
    /// Windows gives only as a device path names no folder (<see cref="SearchLocations.Displayed"/>).</para>
    /// </summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    internal static ReachedFolder? FollowedTo(string path, IVolumeInventory volumes, FileInformation files) =>
        SearchLocations.WhyNotOpened(path, volumes) is null
        && files.FinalPath(path) is { } final
        && SearchLocations.Displayed(final) is { } shown
            ? ReachedFolder.At(shown, volumes)
            : null;

    /// <summary>What the first place holding <paramref name="path"/> says of it, or null where none holds it.</summary>
    /// <param name="path">A full path, in display form.</param>
    public string? WhatHolds(string path)
    {
        var target = ReachedFolder.Comparable(path);

        foreach (var (place, starts, what) in _places)
        {
            if (starts.Any(start => place.HoldsComparable(start, target)))
            {
                return what;
            }
        }

        return null;
    }
}
