namespace Deguffer.Core.Safety;

/// <summary>
/// The parts of a volume's <c>GetVolumeInformation</c> flag word that Deguffer reads.
///
/// <para>Deliberately partial. Windows documents around twenty bits there and two of them decide
/// anything here, so naming the rest would invite a reader to take this for the Win32 constant set
/// and branch on a bit nothing has ever measured. The word is carried unmasked, so a bit with no
/// name here is still present in the value.</para>
/// </summary>
[Flags]
public enum VolumeFeatures : uint
{
    /// <summary>Nothing, which is also what a volume that would not answer is recorded as.</summary>
    None = 0,

    /// <summary>
    /// <c>FILE_SUPPORTS_REPARSE_POINTS</c>. Every local NTFS volume has it, and a volume without it
    /// cannot hold a link, a junction or a Cloud Files placeholder.
    /// </summary>
    ReparsePoints = 0x0000_0080,

    /// <summary>
    /// <c>FILE_SUPPORTS_REMOTE_STORAGE</c>. The volume can hold data that is not on this machine, so
    /// reading a file may fetch it from somewhere else first.
    /// </summary>
    RemoteStorage = 0x0000_0100,
}

/// <summary>
/// Whether a mounted volume can be read, in which way it cannot, or that it was not asked.
///
/// <para>Several states rather than a flag, because the two ways of not being ready send the reader to
/// different places. An empty drive has nothing to say, and a caller is right to pass over it. A
/// volume Windows would not describe may hold anything, and a caller that passed over it in silence
/// would leave out a drive the user can see in File Explorer. See <see cref="PathPresence"/>.</para>
/// </summary>
public enum VolumeReadiness
{
    /// <summary>The volume answers, so its label, size and flags can be asked for.</summary>
    Ready,

    /// <summary>
    /// Nothing to read: an optical drive with no disc, a card reader with no card. Windows answers
    /// that much, and it is a complete answer.
    /// </summary>
    NoMedia,

    /// <summary>
    /// Windows would not say what is at the mount point: an access rule on the root, or a mount it
    /// declines to follow. The volume may hold anything.
    /// </summary>
    Refused,

    /// <summary>
    /// Not asked: a letter standing for a share. Its root is read on the server, and a share that
    /// is not there answers only once the redirector gives up on it, which a list read as a drive
    /// picker opens cannot wait for. No caller asks whether a share is ready, so nothing is lost by
    /// recording that it was never measured rather than inventing an answer.
    /// </summary>
    NotAsked,
}

/// <summary>
/// One volume the machine has mounted, wherever it is mounted.
/// </summary>
/// <param name="RootPath">
/// The mount point callers act on and show, in <c>D:\</c> or <c>C:\Mount\</c> form.
///
/// <para>A volume can be mounted in more than one place at once, and Windows draws no distinction
/// between a drive letter and a folder mount point. One of them still has to be the one a plan
/// targets and the drive picker offers, or the same volume would be listed twice and its Recycle
/// Bin would be planned twice under two names. <see cref="MountPoints"/> is the whole set, for the
/// one question that needs it: whether a given path is on this volume.</para>
/// </param>
/// <param name="Kind">
/// Fixed, removable, network, and so on. Reported rather than filtered, because which kinds a
/// provider may act on is a safety decision belonging to that provider — and a seam that filtered
/// would leave the decision untestable, since no fake could then present the kind being refused.
/// </param>
/// <param name="Readiness">
/// Whether the volume can be read at all. An optical drive with no disc and a card reader with no
/// card are both mounted and both answer no, and a volume whose root Windows would not describe is
/// told apart from them. A share is <see cref="VolumeReadiness.NotAsked"/>.
/// </param>
/// <param name="Label">
/// What the volume is called, or null where it has no label, would not say, or was not asked. Null
/// rather than an empty string, so "unlabelled" is one case at every caller instead of two.
/// </param>
/// <param name="TotalBytes">Capacity, on the same terms.</param>
/// <param name="FreeBytes">
/// What is left of that capacity for this user, on the same terms. The figure a quota allows rather
/// than the raw free space, matching <see cref="IVolumeInventory.SpaceOf"/>: the two are read by the
/// same app, through the same call, and must not disagree.
/// </param>
/// <param name="Features">
/// What the volume says it supports, or <see cref="VolumeFeatures.None"/> where it would not say or
/// was not asked. Reported rather than filtered, for the reason <paramref name="Kind"/> is.
/// </param>
/// <param name="AlsoMountedAt">
/// Every other path this volume is reachable at, or null where there is none — which is the
/// ordinary case, since most volumes wear one drive letter and nothing else.
/// </param>
/// <param name="FeaturesAnswered">
/// Whether the volume answered when it was asked what it supports, so <paramref name="Features"/> is
/// its answer rather than the <see cref="VolumeFeatures.None"/> recorded for a volume that refused or
/// was not asked. Kept apart because the two read alike and mean opposite things to
/// <see cref="CannotHoldLinks"/>: a refusal is never an answer of "nothing supported".
/// </param>
/// <param name="VolumeName">
/// The <c>\\?\Volume{GUID}\</c> name Windows knows the volume by wherever it is mounted, or null for
/// a letter that stands for somewhere else, such as a mapped share. What tells one disk from another
/// when a drive letter moves between them, which is what keeps a scan of one from being compared
/// with a scan of the other.
/// </param>
/// <param name="FileSystem">
/// The name the volume gives its file system, such as <c>NTFS</c>, <c>ReFS</c>, <c>exFAT</c> or
/// <c>FAT32</c>, or null where it would not say or was not asked.
/// </param>
public readonly record struct LocalVolume(
    string RootPath,
    DriveType Kind,
    VolumeReadiness Readiness,
    string? Label = null,
    long? TotalBytes = null,
    long? FreeBytes = null,
    VolumeFeatures Features = VolumeFeatures.None,
    IReadOnlyList<string>? AlsoMountedAt = null,
    string? VolumeName = null,
    bool FeaturesAnswered = false,
    string? FileSystem = null)
{
    /// <summary>
    /// Whether a file's number on this volume stays the file's for as long as the file exists, so it
    /// can name the file from one search to the next: NTFS and ReFS, which keep a number in the
    /// file's record. FAT and exFAT make one up from where the file's entry lies, which a rename, a
    /// move or a defragmentation changes, and a file system that would not name itself, or that
    /// Deguffer does not know, may do the same, so neither counts.
    /// </summary>
    public bool KeepsFileNumbers =>
        string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the volume answers. See <see cref="Readiness"/>.</summary>
    public bool IsReady => Readiness is VolumeReadiness.Ready;

    /// <summary>
    /// Every path this volume is reachable at, <see cref="RootPath"/> first.
    ///
    /// <para>Read by <see cref="HostVolume.For"/>, which is the one caller that asks about a path
    /// rather than about the volume: a folder-mounted volume answers for the paths below its mount
    /// point and nothing else does, so a set that named only the root would hand those paths to the
    /// volume the mount point sits on.</para>
    /// </summary>
    public IEnumerable<string> MountPoints
    {
        get
        {
            yield return RootPath;

            foreach (var mountPoint in AlsoMountedAt ?? [])
            {
                yield return mountPoint;
            }
        }
    }

    /// <summary>
    /// Whether this volume's contents are somewhere else, so that enumerating it downloads the
    /// user's files instead of measuring them.
    ///
    /// <para>A cloud client that mounts its storage through its own driver is a
    /// <see cref="DriveType.Fixed"/>, ready volume that no per-entry test can tell from a disk. A
    /// Google Drive mount's top-level entries were measured carrying nothing but ordinary hidden,
    /// system and normal attributes: no <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>, no
    /// <c>FILE_ATTRIBUTE_OFFLINE</c>, no reparse point. A walker cannot defend itself entry by
    /// entry there, so the location is what has to be refused.</para>
    ///
    /// <para><b>This recognises a mount that advertises the flag, not every cloud client.</b>
    /// Advertising remote storage is a third-party driver's own choice, and nothing in Win32
    /// obliges one to. A mount that does not say so is walked, as every volume was before this
    /// reading existed. Google Drive is the one product whose flag word was measured.</para>
    ///
    /// <para><b>Why these two bits and not the format.</b> The mount advertises remote storage and
    /// supports neither reparse points nor sparse files, and the Cloud Files API builds a
    /// placeholder as a reparse point on a sparse file and supports NTFS alone. So a volume with
    /// this pair cannot be hosting per-file placeholders that would explain the flag innocently,
    /// which is what makes the pair structural rather than incidental. Measured as
    /// <c>0x00000106</c> on a Google Drive mount against <c>0x03E72EFF</c> on seven local NTFS
    /// volumes of the same machine.</para>
    ///
    /// <para><b>The format alone would refuse real disks.</b> A fixed volume that is not NTFS is
    /// also a FAT32 or exFAT disk, a card reader reporting itself fixed, or a mounted image — all
    /// of which are ordinary to walk. A refusal that reached them would take away the tool's
    /// purpose to fix a hazard they do not have.</para>
    /// </summary>
    public bool StoresContentRemotely =>
        Features.HasFlag(VolumeFeatures.RemoteStorage) && !Features.HasFlag(VolumeFeatures.ReparsePoints);

    /// <summary>
    /// Whether the volume said it supports no reparse points, so no symbolic link, junction or mount
    /// can be on a path inside it: FAT and exFAT. Only a volume that answered qualifies; one that
    /// refused the question, or was not asked, may be NTFS and hold links anywhere.
    /// </summary>
    public bool CannotHoldLinks => FeaturesAnswered && !Features.HasFlag(VolumeFeatures.ReparsePoints);

    /// <summary>
    /// Whether this is a disk in or attached to this machine, readable now, whose files are here.
    ///
    /// <para>For a provider that finds an application's cache wherever the user pointed it and
    /// offers it only where no other computer can be using it. A share, and a cloud mount that says
    /// so, may hold a cache that a copy of the same application on another computer is writing to
    /// now, and Deguffer can see only the processes that run on this one.</para>
    /// </summary>
    public bool IsLocalDisk =>
        IsReady && (Kind is DriveType.Fixed or DriveType.Removable) && !StoresContentRemotely;
}

/// <summary>
/// The machine's volumes, behind an interface so a provider that works per volume is testable
/// against directories we build rather than against whatever drives the developer happens to have.
///
/// Separate from <see cref="IUserEnvironment"/> rather than a member on it: that interface is the
/// signed-in user — their profile directories, their <c>PATH</c>, their environment — and the set
/// of mounted volumes is a fact about the hardware instead. Describing one type as "the user and
/// the disks" is G1's own test for two types.
/// </summary>
public interface IVolumeInventory
{
    IReadOnlyList<LocalVolume> Volumes { get; }

    /// <summary>
    /// Where the volume holding <paramref name="path"/> is mounted, asked of the machine at the
    /// moment of the call rather than read from <see cref="Volumes"/>, or null where it will not say.
    ///
    /// <para>Live because its caller is <see cref="VolumeRoot"/>, which has to be right about a
    /// volume mounted a moment ago, and the list is remembered until the next
    /// <see cref="Invalidate"/>. A volume mounted at a folder after the list was read would
    /// otherwise have its paging file and its Recycle Bin read as ordinary folders of the disk the
    /// folder sits on.</para>
    ///
    /// <para>The answer is not always a prefix of the path. A path that passes through a junction
    /// is answered with the volume on the junction's far side, so a caller that reads a position
    /// from the answer has to check that it is one.</para>
    /// </summary>
    string? MountPointOf(string path);

    /// <summary>
    /// Every place the volume mounted at <paramref name="mountPoint"/> is mounted, that one among
    /// them, asked of the machine at the moment of the call. Empty where Windows will not say, which
    /// is a share, a letter standing for somewhere else, and a folder nothing is mounted at.
    ///
    /// <para>Live for the reason <see cref="MountPointOf"/> is. Its caller is
    /// <see cref="VolumeRoot.Places"/>, which finds every other path an item is reachable at, so that
    /// a refusal written about <c>C:\Windows</c> holds for the same folder reached through another
    /// mount of the system volume. A list remembered from before that mount was made would name
    /// nothing and refuse nothing.</para>
    /// </summary>
    /// <param name="mountPoint">A mount point, as <see cref="MountPointOf"/> answers one.</param>
    IReadOnlyList<string> MountPointsOf(string mountPoint);

    /// <summary>
    /// The folder the drive letter <paramref name="driveRoot"/> stands for, where <c>subst</c> made
    /// it one, asked of the machine at the moment of the call, or null where the letter is a
    /// volume's own, a mapped share, or not a letter at all.
    ///
    /// <para>For <see cref="VolumeRoot.Places"/>, for the reason <see cref="MountPointsOf"/> is: a
    /// letter standing for <c>C:\Users\testuser</c> reaches that profile's application data, and
    /// Windows names no volume for such a letter, so no mount point leads back to it.</para>
    /// </summary>
    /// <param name="driveRoot">The root of a path, as <see cref="Path.GetPathRoot(string)"/> answers it.</param>
    string? SubstituteOf(string driveRoot);

    /// <summary>
    /// The capacity of the volume holding <paramref name="path"/>, and what is left of it for this
    /// user, asked of the machine at the moment of the call, or null where the volume will not say.
    ///
    /// <para>Live rather than read from <see cref="Volumes"/>, for the reason
    /// <see cref="MountPointOf"/> is and one of its own: this is the figure a clean is measured by,
    /// read either side of the run, and a remembered list would report the space as it was before
    /// the run on both sides.</para>
    ///
    /// <para>Asked of the volume the path is really on, which <see cref="Path.GetPathRoot(string)"/>
    /// and <c>DriveInfo</c> cannot answer: both reduce a path to a drive letter, so a path on a volume
    /// mounted at a folder would be measured against the disk that folder sits on.</para>
    /// </summary>
    (long Total, long Free)? SpaceOf(string path);

    /// <summary>
    /// Discard the remembered list, so a drive mounted while the app was open is seen on the next
    /// preview. Called at the start of a planning pass, as <see cref="IUserEnvironment.Invalidate"/>
    /// is.
    ///
    /// <para>Not on the UI thread: the next read of <see cref="Volumes"/> then asks every volume the
    /// machine has mounted, on whichever thread reads it. The window calls
    /// <see cref="RefreshAsync"/>.</para>
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Read the volumes again off the calling thread, and put what was read in place of the
    /// remembered list. Until then <see cref="Volumes"/> answers with the list as it was, so where a
    /// list is remembered, a reader on the UI thread does not wait for this reading. Where none is,
    /// before the first reading or after an <see cref="Invalidate"/>, a read of
    /// <see cref="Volumes"/> still asks the machine on the thread that reads it.
    ///
    /// <para>A list read across an <see cref="Invalidate"/> is not kept, because the caller that
    /// invalidated asked for a reading taken after it. The machine is read again instead.</para>
    /// </summary>
    /// <returns>The list now remembered, so a caller on the UI thread need not read <see cref="Volumes"/> to see it.</returns>
    Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct);
}
