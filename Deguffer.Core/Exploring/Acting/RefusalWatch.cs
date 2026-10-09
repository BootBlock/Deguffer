using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Where, below a folder <see cref="ExploreActionPolicy.RefusedAtAndBelow"/> does not refuse, it can
/// refuse a child: so a caller going through a whole drive asks it about the children that could
/// differ from their folder, and about no others.
///
/// <para><b>Why that is a handful.</b> The policy's answer for a child changes from its folder's only
/// where a rule starts at the child: a name at the top of a volume, Outlook's mail stores and the
/// folder it saves them in, which are refused by name wherever they are, or a region of the table,
/// refusing or permitting, at or inside the child. A folder that
/// neither sits at the top of a volume nor holds a region's folder has every region that covers any
/// child already covering itself, and no permission below it to carve anything out, so each child is
/// answered as the folder was. Only the folders on the way to a region's folder, and the top of the
/// volume, have children to ask about.</para>
///
/// <para>Text is compared with the region's folder at every path it is reachable at. A folder reached
/// by a path no region is named at is answered as its parent was, which can only search a place that
/// would have been passed over, and never passes over a place that would have been searched.</para>
/// </summary>
public sealed class RefusalWatch
{
    private readonly bool _rootIsVolumeTop;
    private readonly IReadOnlyList<string> _boundaries;

    internal RefusalWatch(bool rootIsVolumeTop, IReadOnlyList<string> boundaries)
    {
        _rootIsVolumeTop = rootIsVolumeTop;
        _boundaries = boundaries;
    }

    /// <summary>
    /// Whether the child named <paramref name="childName"/> of <paramref name="parent"/> has to be
    /// asked about, where <paramref name="parent"/> was not refused.
    /// </summary>
    /// <param name="parent">The folder holding the child, in display form.</param>
    /// <param name="parentIsRoot">Whether <paramref name="parent"/> is the folder the watch was made for.</param>
    public bool MayRefuse(string parent, bool parentIsRoot, string childName) =>
        (parentIsRoot && _rootIsVolumeTop)
        || childName.Equals(OutlookDataFiles.SavedDataFilesFolder, StringComparison.OrdinalIgnoreCase)
        || MailStore.Is(childName)
        || _boundaries.Any(boundary => LongPath.Contains(parent, boundary));
}
