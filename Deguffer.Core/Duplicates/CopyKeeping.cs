using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Whether a copy is one a group can count on keeping (§7.4), mark or no mark: on this device and
/// not online-only, not refused, not in the temporary folder, not where a Storage clean deletes,
/// and, unless its location is a reference, on an internal drive and outside a cloud folder.
///
/// <para><b>Each exclusion is a copy that can go without anyone choosing it to.</b> A program can
/// empty its temporary folder, Storage deletes what its providers name, a removable drive is
/// unplugged, and a cloud copy can be removed from another device. Making a location a reference is
/// how a user says the copy on a removable drive or in a cloud folder is the one they keep, and it
/// lifts those two exclusions and no other.</para>
///
/// <para><b>Internal is what the disk's bus says, never <see cref="DriveType"/>.</b> Most USB disks
/// report themselves as fixed. A drive is internal only where its class is NVMe, solid state or
/// rotational; removable media, a virtual disk (whose file may itself be on a USB disk), and a drive
/// Windows would not describe, or whose disks are of different kinds, are not counted on.</para>
///
/// <para><b>A cloud folder is one Windows lists as a sync root.</b> Where Windows will not list them,
/// no copy counts as outside one, so only a reference copy can be kept.</para>
///
/// <para><b>A file with several names</b> is kept where any one of its names could be.</para>
/// </summary>
public sealed class CopyKeeping
{
    private const string NoSyncRoots =
        "Windows would not list this computer's cloud folders, so this copy may be in one, where it can be "
        + "removed from another device.";

    private readonly CopyRefusals _refusals;
    private readonly ResolvedPlaces _temporary;
    private readonly ResolvedPlaces _cleaned;
    private readonly ResolvedPlaces? _cloud;
    private readonly Func<LocalVolume, StorageMedia> _media;

    /// <param name="temporary">The account's temporary folder.</param>
    /// <param name="cleaned">Where Storage's cleans delete, each naming the clean.</param>
    /// <param name="cloud">The sync roots, each naming its folder, or null where Windows would not list them.</param>
    /// <param name="media">What each volume's disks are, as the search's media cache answers.</param>
    internal CopyKeeping(
        CopyRefusals refusals,
        ResolvedPlaces temporary,
        ResolvedPlaces cleaned,
        ResolvedPlaces? cloud,
        Func<LocalVolume, StorageMedia> media)
    {
        _refusals = refusals;
        _temporary = temporary;
        _cleaned = cleaned;
        _cloud = cloud;
        _media = media;
    }

    /// <summary>The refusals the keeping rule includes, which are the refusals of marking too.</summary>
    public CopyRefusals Refusals => _refusals;

    /// <summary>
    /// Why <paramref name="copy"/> may not be marked, or null where it may be: it is never marked (a
    /// reference copy, a file with several names) or it is refused.
    /// </summary>
    public string? WhyNotMarked(DuplicateCandidate copy) => CopyRefusals.WhyNeverMarked(copy) ?? _refusals.WhyRefused(copy);

    /// <summary>
    /// Why <paramref name="copy"/> may not be marked and why it may not be kept, as a page lists them
    /// beside it: the second only where it says something the first does not, since a refused copy is
    /// not kept for the same reason.
    /// </summary>
    public CopyStanding Standing(DuplicateCandidate copy)
    {
        var notMarked = WhyNotMarked(copy);
        var notKept = WhyNotKept(copy);

        return new CopyStanding(notMarked, notKept == notMarked ? null : notKept);
    }

    /// <summary>Why <paramref name="copy"/> cannot be the copy a group keeps, or null where it can be.</summary>
    public string? WhyNotKept(DuplicateCandidate copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        // Online-only is the file's, not a name's.
        if (copy.Storage.HasFlag(FileStorage.CloudOnly))
        {
            return _refusals.WhyRefused(copy);
        }

        if (copy.Role != LocationRole.Reference && WhyNotInternal(copy.Volume) is { } removable)
        {
            return removable;
        }

        string? first = null;

        foreach (var name in copy.Names)
        {
            if (WhyNotKept(name, copy.Role) is not { } why)
            {
                return null;
            }

            first ??= why;
        }

        return first;
    }

    /// <summary>
    /// The cloud folder <paramref name="copy"/> is in, in a sentence, or null where it is in none. A
    /// copy is in one where Windows would not list them. No rule marks a copy for which this answers.
    /// </summary>
    public string? CloudFolderOf(DuplicateCandidate copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (_cloud is null)
        {
            return NoSyncRoots;
        }

        return copy.Names.Select(_cloud.WhatHolds).FirstOrDefault(what => what is not null);
    }

    private string? WhyNotKept(string name, LocationRole role) =>
        _refusals.WhyRefused(name)
        ?? _temporary.WhatHolds(name)
        ?? _cleaned.WhatHolds(name)
        ?? (role == LocationRole.Reference ? null : _cloud is null ? NoSyncRoots : _cloud.WhatHolds(name));

    private string? WhyNotInternal(LocalVolume volume) => _media(volume) switch
    {
        StorageMedia.Nvme or StorageMedia.SolidState or StorageMedia.Rotational => null,
        StorageMedia.Removable =>
            "This is on a removable or USB drive, which can be unplugged, so it is not counted on as the copy kept.",
        StorageMedia.Virtual =>
            "This is on a virtual disk, which can be detached, and whose file may itself be on a removable drive.",
        _ => "Windows would not say what kind of drive this is, so it is not counted on to stay attached.",
    };
}
