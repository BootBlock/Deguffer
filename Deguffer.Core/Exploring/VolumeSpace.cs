using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// How large a scanned volume is and how much of it is free, for the blocks a treemap draws beside
/// a scan of the whole of it. <see cref="None"/> where the scan did not cover a whole volume.
///
/// <para>Only for a whole volume. Free space is in proportion to a volume, and beside one folder of
/// it the block would say the folder shares its volume with that much room, which is true of every
/// folder on it and tells the reader nothing about this one — and on a nearly empty drive it would
/// shrink the folder the reader asked about to a corner.</para>
///
/// <para>The capacity comes with it because free space alone would overstate itself. What a scan
/// counts is a lower bound (§7.1): folders it could not read, and the file system's own records,
/// are in use and in no folder. Laid beside the scan, free space would take the share of the
/// picture those bytes belong to, so the treemap draws them as a block of their own, worked out
/// from the capacity.</para>
///
/// <para>Part of that block is space Windows states the size of: the restore points and shadow
/// copies, and reserved storage. Those are drawn apart from it under their own names, from Windows'
/// figures, and never from a difference — see <see cref="Parts"/>.</para>
/// </summary>
/// <param name="TotalBytes">The volume's capacity.</param>
/// <param name="FreeBytes">
/// What is left of it for this user, which is what <see cref="LocalVolume.FreeBytes"/> reports.
/// </param>
/// <param name="Hidden">What Windows stated about the space no folder accounts for.</param>
/// <param name="CountedSystemVolumeInformation">
/// Whether the scan counted what is inside <c>System Volume Information</c>, which is where the
/// shadow copy storage is kept. Only the file table can, since Windows refuses even an
/// administrator a walk of that folder.
/// </param>
public readonly record struct VolumeSpace(
    long TotalBytes,
    long FreeBytes,
    HiddenSpace Hidden = default,
    bool CountedSystemVolumeInformation = false)
{
    public const string SystemVolumeInformation = "System Volume Information";

    public static VolumeSpace None { get; } = new(0, 0);

    /// <summary>
    /// The space on the volume <paramref name="scannedRoot"/> is the top of, or <see cref="None"/>
    /// where it is a folder inside a volume, where no volume in <paramref name="volumes"/> holds it,
    /// or where the volume would not state both figures.
    ///
    /// <para>Whether the root is the top of its volume is <see cref="VolumeRoot"/>'s question, which
    /// answers for a volume mounted at a folder and for each of a volume's mount points, and is
    /// written once for the safety rules that ask it too.</para>
    ///
    /// <para><b>Asked of the folder a <c>subst</c> letter stands for</b>, as
    /// <see cref="HostVolume.For"/> answers the volume of. <c>S:\</c> is the top of a drive letter, and
    /// with <c>S:</c> standing for <c>C:\Users\testuser\src</c> it is a folder of <c>C:</c>: read as a
    /// top, its scan was given the whole of <c>C:</c>'s space, and kept as a scan of that volume.</para>
    /// </summary>
    public static VolumeSpace Of(IVolumeInventory volumes, string scannedRoot)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        return Path.IsPathFullyQualified(scannedRoot)
            && VolumeRoot.Below(volumes, VolumeRoot.Followed(volumes, scannedRoot)) is null
            && HostVolume.For(volumes, scannedRoot) is { TotalBytes: { } total, FreeBytes: { } free }
                ? new VolumeSpace(total, free)
                : None;
    }

    /// <summary>
    /// <see cref="Of"/> for a finished scan of <paramref name="scannedRoot"/>, with what Windows
    /// states about the space no folder accounts for, read once from <paramref name="hidden"/>.
    /// Nothing is asked of Windows where the scan did not cover a whole volume, because nothing is
    /// drawn beside it.
    /// </summary>
    /// <param name="tree">What the scan found, which says whether it counted the shadow copy storage.</param>
    public static async Task<VolumeSpace> ReadAsync(
        IVolumeInventory volumes,
        IHiddenSpaceSource hidden,
        string scannedRoot,
        ExploreTree tree,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(tree);

        var space = Of(volumes, scannedRoot);

        return space == None
            ? None
            : space with
            {
                Hidden = await hidden.ReadAsync(LongPath.Display(scannedRoot), ct).ConfigureAwait(false),
                CountedSystemVolumeInformation = Counted(tree),
            };
    }

    /// <summary>
    /// What is drawn beside a scan that counted <paramref name="scannedBytes"/>: the space the volume
    /// says is in use and the scan did not count, divided into what Windows names and what is left.
    ///
    /// <para><b>A named part is taken out whole or not at all.</b> It is drawn at the size Windows
    /// states, and only where that fits in the space the scan left over. A part that does not fit has
    /// been counted, at least partly, by the scan, and drawing it cut down would show a size Windows
    /// never stated beside a label naming the one it did. It stays in the unaccounted block, whose
    /// note gives the figure.</para>
    ///
    /// <para>The reserve is taken first, because Windows reports it apart from the space in use
    /// (see <see cref="ReservedStorage"/>). The shadow copy storage is taken only where the scan
    /// counted nothing inside <c>System Volume Information</c>, where it is kept, so bytes the scan
    /// counted there are never drawn a second time.</para>
    ///
    /// <para><b>A scan that counted more than is in use leaves nothing, and says by how much.</b>
    /// Drawn by space on disk, the file table cannot count more than the volume holds. What can is a
    /// walk, which counts a hard link once per name and a CompactOS file at its length, and files
    /// written while the scan ran. The block is clamped at zero, and
    /// <see cref="VolumeParts.Overcounted"/> keeps what the clamp hid, so the page can say so rather
    /// than draw a picture that only appears to add up.</para>
    /// </summary>
    public VolumeParts Parts(long scannedBytes)
    {
        var inUse = TotalBytes - FreeBytes;
        var left = Math.Max(0, inUse - scannedBytes);

        var reserved = Take(Hidden.Reserved is { Statement: Statement.Stated, Bytes: var bytes } ? bytes : 0);
        var shadowCopies = Take(ShadowCopiesBeside);

        return new VolumeParts(shadowCopies, reserved, left, FreeBytes, Math.Max(0, scannedBytes - inUse));

        long Take(long part)
        {
            if (part <= 0 || part > left)
            {
                return 0;
            }

            left -= part;
            return part;
        }
    }

    /// <summary>
    /// What Windows states the shadow copy storage takes from the volume, where the scan did not
    /// count it, or zero.
    /// </summary>
    public long ShadowCopiesBeside =>
        !CountedSystemVolumeInformation && Hidden.ShadowCopies is { Statement: Statement.Stated } shadow
            ? shadow.AllocatedBytes
            : 0;

    /// <summary>
    /// Whether the scan counted anything inside the volume's <c>System Volume Information</c>, right
    /// below the root: some of its bytes, or every size in it, which an empty folder has.
    ///
    /// <para>Partly counted is counted. Taking the storage out beside a folder the scan read part of
    /// could draw what it read twice, and leaving it in the unaccounted block only leaves Windows'
    /// figure to that block's note and to the folder's own description.</para>
    /// </summary>
    private static bool Counted(ExploreTree tree)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.IsDirectory(child)
                && string.Equals(tree.NameOf(child), SystemVolumeInformation, StringComparison.OrdinalIgnoreCase))
            {
                return tree.SizeOf(child) > 0 || !tree.HasUnknownSizeBelow(child);
            }
        }

        return false;
    }
}
