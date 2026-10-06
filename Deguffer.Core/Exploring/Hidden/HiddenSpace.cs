namespace Deguffer.Core.Exploring.Hidden;

/// <summary>
/// What Windows said when it was asked for one of its figures.
///
/// <para>Three answers rather than a nullable number, because the two ways of not having a figure
/// send the reader to different places. A refusal is fixed by scanning as administrator, and the
/// page offers that. A figure Windows would not give for any other reason has no fix to offer, and
/// saying "scan as administrator" there would send the reader round in a circle.</para>
/// </summary>
public enum Statement
{
    /// <summary>
    /// Windows gave no figure, or was not asked: the volume is not one it keeps these for, or the
    /// call failed for a reason administrator rights would not change.
    /// </summary>
    NotStated,

    /// <summary>Windows stated the figure.</summary>
    Stated,

    /// <summary>Windows states the figure only to a process with administrator rights.</summary>
    NeedsElevation,
}

/// <summary>
/// The space a volume keeps for restore points and shadow copies, in Windows' own three figures:
/// the ones <c>vssadmin list shadowstorage</c> prints and System Protection's limit is set against.
///
/// <para>Only the storage that is <em>on</em> this volume, whichever volumes it serves. A drive's
/// shadow copies can be kept on another drive, and it is the drive holding them that is short of
/// the space.</para>
/// </summary>
/// <param name="UsedBytes">What the restore points and shadow copies kept now occupy.</param>
/// <param name="AllocatedBytes">
/// What Windows has taken from the volume to keep them in, which is at least
/// <paramref name="UsedBytes"/>. This is the figure the volume's free space is short by, so it is the
/// one drawn.
/// </param>
/// <param name="MaximumBytes">
/// How far System Protection lets it grow, or null where Windows sets no limit.
/// </param>
public readonly record struct ShadowStorage(
    Statement Statement,
    long UsedBytes = 0,
    long AllocatedBytes = 0,
    long? MaximumBytes = null)
{
    public static ShadowStorage Refused { get; } = new(Statement.NeedsElevation);
}

/// <summary>
/// The space Windows keeps back on a volume for updates, temporary files and caches (reserved
/// storage), in the figure <c>GetDiskSpaceInformation</c> gives for it.
///
/// <para>Windows counts it apart from the space in use. Its documented accounting is that a volume's
/// capacity is its free space, the space in use, a pool share, and the space it reserves, with the
/// storage reserve inside the last — and writing a file moves its size into the space in use and
/// leaves this figure where it was. So it is never part of what a scan counts, and it is short of
/// the free space the volume reports, which puts it in the space a scan cannot account for.</para>
/// </summary>
public readonly record struct ReservedStorage(Statement Statement, long Bytes = 0);

/// <summary>
/// What Windows states about space on a volume that no folder accounts for. Read once per scan of a
/// whole volume, through <see cref="IHiddenSpaceSource"/>.
/// </summary>
public readonly record struct HiddenSpace(ShadowStorage ShadowCopies, ReservedStorage Reserved)
{
    /// <summary>
    /// Nothing stated about either, which is what a scan of anything but a whole volume has.
    /// </summary>
    public static HiddenSpace None { get; } = default;

    /// <summary>
    /// Whether a figure was refused for want of administrator rights, so that scanning as
    /// administrator would add it to the picture.
    /// </summary>
    public bool NeedsElevation =>
        ShadowCopies.Statement is Statement.NeedsElevation || Reserved.Statement is Statement.NeedsElevation;
}

/// <summary>
/// Reads what Windows states about the space on a volume that no folder accounts for.
///
/// <para>A seam because the figures come from a system service and the file system, and a test of
/// what the picture does with them has to choose them: a refusal, a figure larger than the space the
/// scan left over, a volume with none at all. <see cref="WindowsHiddenSpace"/> is the machine's.</para>
/// </summary>
public interface IHiddenSpaceSource
{
    /// <param name="volumeRoot">The volume's mount point, ending in a separator.</param>
    Task<HiddenSpace> ReadAsync(string volumeRoot, CancellationToken ct);
}
