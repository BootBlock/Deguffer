using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Whether a drive or folder the user picked can join the Duplicates page's list of locations
/// (§7.4), and what they are told where it cannot.
///
/// <para>Only what is known before anything is opened is decided here. Whether a folder leads to a
/// share, a network drive or a cloud drive is decided when the search resolves it
/// (<see cref="SearchLocations"/>), which names each location it would not search, because a link can
/// lead there whatever the path the user picked says.</para>
/// </summary>
public static class LocationChoice
{
    public const string NotOnDisk =
        "That is not a folder on a disk, so Deguffer cannot search it. Choose a drive, or a folder on one of this computer's drives.";

    public const string AlreadyListed = "That location is already in the list.";

    /// <summary>
    /// Why the folder at <paramref name="picked"/>, as the picker returned it, cannot be added to
    /// <paramref name="listed"/>, or null where it can. The same path twice is refused, compared case
    /// and all, because a case-sensitive folder can hold <c>Photos</c> and <c>photos</c>, and those are
    /// two places.
    ///
    /// <para>What a folder on a disk is, <see cref="PickedFolder.OnDisk"/> says, as it does for every
    /// page. The words are this page's own, because <see cref="PickedFolder.NotOnDisk"/> offers a
    /// network share, and the search refuses one.</para>
    /// </summary>
    public static string? WhyNotAdded(IEnumerable<SearchLocation> listed, string picked)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(picked);

        if (PickedFolder.OnDisk(picked) is null)
        {
            return NotOnDisk;
        }

        return listed.Any(location => location.Path.Equals(picked, StringComparison.Ordinal)) ? AlreadyListed : null;
    }

    /// <summary>
    /// Why the whole of <paramref name="drive"/> cannot be added to <paramref name="listed"/>, or null
    /// where it can: a drive Explore refuses to scan is refused here for the same reason, a cloud
    /// drive's files included, since reading them would download them.
    /// </summary>
    public static string? WhyNotAdded(IEnumerable<SearchLocation> listed, DriveChoice drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        return drive.Refusal ?? WhyNotAdded(listed, drive.RootPath);
    }
}
