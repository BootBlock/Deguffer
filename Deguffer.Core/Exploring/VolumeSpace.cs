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
    /// </summary>
    public static VolumeSpace Of(IVolumeInventory volumes, string scannedRoot)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        return Path.IsPathFullyQualified(scannedRoot)
            && VolumeRoot.Below(volumes, scannedRoot) is null
            && HostVolume.For(volumes, scannedRoot) is { TotalBytes: { } total, FreeBytes: { } free }
                ? new VolumeSpace(total, free)
                : None;
    }

    /// <summary>
    /// <see cref="Of"/> for <paramref name="tree"/>, with what Windows states about the space no
    /// folder accounts for, read once from <paramref name="hidden"/>. Nothing is asked of Windows
    /// where the scan did not cover a whole volume, because nothing is drawn beside it.
    /// </summary>
    public static async Task<VolumeSpace> ReadAsync(
        IVolumeInventory volumes,
        IHiddenSpaceSource hidden,
        ExploreTree tree,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(tree);

        var space = Of(volumes, tree.RootPath);

        return space == None
            ? None
            : space with
            {
                Hidden = await hidden.ReadAsync(LongPath.Display(tree.RootPath), ct).ConfigureAwait(false),
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
    /// <para>The reserve is taken first, because Windows counts it apart from every file and no scan
    /// can have counted it. The shadow copy storage is taken only where the scan did not count
    /// <c>System Volume Information</c>, where it is kept, so the same bytes are never drawn
    /// twice.</para>
    /// </summary>
    public VolumeParts Parts(long scannedBytes)
    {
        var left = Math.Max(0, TotalBytes - FreeBytes - scannedBytes);

        var reserved = Take(Hidden.Reserved is { Statement: Statement.Stated, Bytes: var bytes } ? bytes : 0);
        var shadowCopies = Take(ShadowCopiesBeside);

        return new VolumeParts(shadowCopies, reserved, left, FreeBytes);

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
    /// What the volume says is in use and <paramref name="scannedBytes"/> does not include, less
    /// what Windows names, or zero where the scan counted as much as that or more. A scan can count
    /// more, because it adds up the length of every file, and a compressed or sparse file occupies
    /// less than its length.
    /// </summary>
    public long UnaccountedBytes(long scannedBytes) => Parts(scannedBytes).Unaccounted;

    /// <summary>
    /// Whether the scan counted the contents of the volume's <c>System Volume Information</c>: it is
    /// in the tree, right below the root, and every size inside it was established.
    /// </summary>
    private static bool Counted(ExploreTree tree)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.IsDirectory(child)
                && string.Equals(tree.NameOf(child), SystemVolumeInformation, StringComparison.OrdinalIgnoreCase))
            {
                return !tree.HasUnknownSizeBelow(child);
            }
        }

        return false;
    }
}
