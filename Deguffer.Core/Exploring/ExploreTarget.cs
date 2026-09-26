using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page is pointed at: a drive from the picker, and a folder on it where one was
/// chosen. Each way the page can be re-pointed is a method here, because each decides what happens
/// to the half it was not asked about, and getting that wrong is the page stating one target while
/// scanning another.
///
/// <para>A value rather than the page's own state, so the rules can be asserted without the page.
/// The page holds the two halves as the properties its controls bind to, and asks this where the
/// next move leaves them.</para>
/// </summary>
/// <param name="Drive">The root of the drive the picker names, or null where it names none.</param>
/// <param name="Folder">The folder a scan is scoped to, or null for the whole drive.</param>
public readonly record struct ExploreTarget(string? Drive, string? Folder)
{
    /// <summary>
    /// What the next scan covers: the chosen folder, and the whole drive where none was chosen.
    /// Choosing a folder is the more specific act, so it wins, and the drive box follows it rather
    /// than contradicting it — see <see cref="ScopedTo"/>.
    /// </summary>
    public string? Root => Folder ?? Drive;

    public bool IsScopedToFolder => Folder is not null;

    /// <summary>
    /// Why Explore will not scan <see cref="Root"/>, or null where it will.
    ///
    /// <para>Asked of the scan's root rather than of the drive, because the volume decides and a
    /// folder chosen through the picker can be on one the box is not naming. Scoping to a folder on
    /// a cloud mount is the same hazard as scanning the whole of it, and it is the route a reader
    /// takes next when the drive is refused.</para>
    ///
    /// <para>Asked of the machine's volumes through <see cref="HostVolume"/> rather than of the
    /// picker's list, which answers only for the volumes the page chose to offer. A target on a
    /// volume the inventory says nothing about — a share, most often — is not refused: nothing
    /// measured its flags, and refusing on no reading would be a guess.</para>
    /// </summary>
    public string? Refusal(IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        return Root is { } root && HostVolume.For(volumes, root) is { StoresContentRemotely: true }
            ? DriveChoice.RemoteStorageRefusal
            : null;
    }

    /// <summary>Whether there is something to scan and nothing refuses it.</summary>
    public bool IsScannable(IVolumeInventory volumes) => Root is not null && Refusal(volumes) is null;

    /// <summary>
    /// Whether <paramref name="other"/> scans what this does. The elevation offer describes what
    /// pressing Scan would cover, so it is put back only where that changes.
    /// </summary>
    public bool ScansTheSameAs(ExploreTarget other) =>
        string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A drive chosen in the picker. Choosing a drive is choosing to scan the whole of it, so any
    /// folder scope goes with it: the alternative leaves both set, and the page then states one
    /// target while scanning another.
    /// </summary>
    public ExploreTarget Choosing(string? drive) => new(drive, null);

    /// <summary>The whole of the drive already chosen, with the folder scope dropped.</summary>
    public ExploreTarget WholeDrive() => new(Drive, null);

    /// <summary>
    /// Scoped to <paramref name="folder"/>, with the drive box moved to the volume holding it.
    ///
    /// <para>The two controls describe a single choice, and a drive box naming a volume the scan is
    /// not on is the kind of disagreement a reader takes for a bug. A folder on a share, or on a
    /// volume the box does not list, has no entry to move to and leaves the box as it was.</para>
    /// </summary>
    /// <param name="holdingDrive">
    /// The picker's entry for the volume holding <paramref name="folder"/>, or null where it offers
    /// none. See <see cref="DriveList.Holding"/>.
    /// </param>
    public ExploreTarget ScopedTo(string folder, string? holdingDrive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return new(holdingDrive ?? Drive, folder);
    }

    /// <summary>
    /// Where a fresh reading of the volumes leaves the page, given the entry the picker now names.
    ///
    /// <para>A reading that hands the same volume back is not a choice, and keeps the folder. One
    /// that cannot — the drive was unplugged, the disc ejected, the volume re-locked — has moved the
    /// page to a volume nobody picked, and that is a change of drive however it came about, so the
    /// folder goes as it would for a drive picked by hand. A page that named no drive before keeps
    /// its folder: the reading has given the box something to show, and the scan was never pointed
    /// at a drive for it to have moved from.</para>
    /// </summary>
    /// <param name="drive">What <see cref="DriveList.Choose"/> answered for <see cref="Drive"/>.</param>
    public ExploreTarget AfterReading(string? drive) =>
        Drive is null || string.Equals(Drive, drive, StringComparison.OrdinalIgnoreCase)
            ? new(drive, Folder)
            : Choosing(drive);

    /// <summary>
    /// Where an earlier instance was pointed, so an elevated replacement resumes the scan the user
    /// asked for, or null where nothing of it can be restored.
    ///
    /// <para>Null means the drive has gone and no folder was named, and the caller must not scan on
    /// the strength of it. The box still holds whichever volume it defaulted to, and scanning a
    /// volume the user never chose is worse than scanning nothing.</para>
    /// </summary>
    /// <param name="offeredDrive">
    /// The picker's entry for the drive asked for, or null where it is no longer mounted — which is
    /// left alone rather than forced into the box, where it would name a volume the box cannot offer.
    /// </param>
    public ExploreTarget? Pointing(string? offeredDrive, string? folder) =>
        offeredDrive is null && folder is null ? null : new(offeredDrive ?? Drive, folder);
}
