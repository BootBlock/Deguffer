using System.Runtime.InteropServices;

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
/// One volume the machine has mounted under a drive letter.
/// </summary>
/// <param name="RootPath">Where it is mounted, in <c>D:\</c> form.</param>
/// <param name="Kind">
/// Fixed, removable, network, and so on. Reported rather than filtered, because which kinds a
/// provider may act on is a safety decision belonging to that provider — and a seam that filtered
/// would leave the decision untestable, since no fake could then present the kind being refused.
/// </param>
/// <param name="IsReady">
/// Whether the volume can be read at all. An optical drive with no disc and a card reader with no
/// card are both mounted and both answer no, and reading anything else about them throws.
/// </param>
/// <param name="Label">
/// What the volume is called, or null where it has no label, would not say, or was not asked. Null
/// rather than an empty string, so "unlabelled" is one case at every caller instead of two.
/// </param>
/// <param name="TotalBytes">Capacity, on the same terms.</param>
/// <param name="FreeBytes">
/// What is left of that capacity for this user, on the same terms. The figure a quota allows rather
/// than the raw free space, matching <c>FreeSpace.ForPath</c>: the two are read by the same app and
/// must not disagree.
/// </param>
/// <param name="Features">
/// What the volume says it supports, or <see cref="VolumeFeatures.None"/> where it would not say or
/// was not asked. Reported rather than filtered, for the reason <paramref name="Kind"/> is.
/// </param>
public readonly record struct LocalVolume(
    string RootPath,
    DriveType Kind,
    bool IsReady,
    string? Label = null,
    long? TotalBytes = null,
    long? FreeBytes = null,
    VolumeFeatures Features = VolumeFeatures.None)
{
    /// <summary>
    /// Whether this volume's contents are somewhere else, so that enumerating it downloads the
    /// user's files instead of measuring them.
    ///
    /// <para>A cloud client that mounts its storage through its own driver — Google Drive and
    /// pCloud both do — is a <see cref="DriveType.Fixed"/>, ready volume that no per-entry test can
    /// tell from a disk. Its top-level entries were measured carrying nothing but ordinary hidden,
    /// system and normal attributes: no <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>, no
    /// <c>FILE_ATTRIBUTE_OFFLINE</c>, no reparse point. A walker cannot defend itself entry by
    /// entry there, so the location is what has to be refused.</para>
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
    /// Discard the remembered list, so a drive mounted while the app was open is seen on the next
    /// preview. Called at the start of a planning pass, as <see cref="IUserEnvironment.Invalidate"/>
    /// is.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed partial class VolumeInventory : IVolumeInventory
{
    /// <summary>
    /// The one instance the app runs with (G5). Stateless apart from the list it remembers, and
    /// that list describes the machine rather than any one caller.
    /// </summary>
    public static readonly VolumeInventory Current = new();

    private readonly Lock _gate = new();

    private IReadOnlyList<LocalVolume>? _volumes;

    /// <summary>
    /// Memoised for the life of a planning pass. Enumerating drives probes every mounted device,
    /// which for an optical drive means waiting on the hardware, and a provider asks the same
    /// question from both <c>IsPresentAsync</c> and <c>PlanAsync</c> (G4).
    /// </summary>
    public IReadOnlyList<LocalVolume> Volumes
    {
        get
        {
            lock (_gate)
            {
                return _volumes ??= Read();
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _volumes = null;
        }
    }

    private static IReadOnlyList<LocalVolume> Read()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            // Nothing rather than a partial view, on the same reasoning as ChildDirectories.Under:
            // a caller decides what the machine holds from what it is handed.
            return [];
        }

        return [.. drives.Select(Describe)];
    }

    /// <summary>
    /// IsReady is the one member that answers for an empty drive instead of throwing, which is why
    /// it gates everything else read here.
    ///
    /// <para>A network volume is described by its mount point alone. The label and the two space
    /// figures each cost a round trip to the server, <see cref="Volumes"/> is read under a lock and
    /// on the UI thread, and no caller wants them for a share: the picker refuses network volumes
    /// outright and <c>RecycleBinProvider</c> takes fixed ones only. This declines a cost, and
    /// filters nothing — which kinds a caller may act on stays that caller's decision.</para>
    /// </summary>
    private static LocalVolume Describe(DriveInfo drive)
    {
        var root = drive.RootDirectory.FullName;

        // Read once. IsReady probes the volume rather than reading a field, and on a share that
        // probe is the round trip this method exists to spend as few of as it can. Two reads can
        // also disagree, which would report a volume as ready with nothing else known about it.
        var ready = drive.IsReady;

        if (!ready || drive.DriveType == DriveType.Network)
        {
            return new LocalVolume(root, drive.DriveType, ready);
        }

        // Before the label and the space, and kept on both paths below, because this is the one
        // reading a caller refuses a whole volume on: a drive that declined its label would
        // otherwise be described as having said nothing about remote storage either, and be walked.
        var features = FeaturesOf(root);

        try
        {
            return new LocalVolume(
                root,
                drive.DriveType,
                IsReady: true,
                Label: string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                TotalBytes: drive.TotalSize,
                FreeBytes: drive.AvailableFreeSpace,
                Features: features);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The medium can go away between IsReady and these reads, and a volume can refuse the
            // label query outright. Where it is mounted is still true, and it is the part callers
            // act on, so the volume is reported without the detail rather than dropped.
            return new LocalVolume(root, drive.DriveType, IsReady: true, Features: features);
        }
    }

    /// <summary>
    /// What <paramref name="root"/> says it supports, or <see cref="VolumeFeatures.None"/> where it
    /// would not say.
    ///
    /// <para><c>DriveInfo</c> exposes the format name and none of the flags, so this is the only
    /// route to them. Both string buffers are passed as null, which the call documents as
    /// permitted: nothing here wants the volume's name or its format, and the allocations would be
    /// spent once per mounted device.</para>
    /// </summary>
    private static VolumeFeatures FeaturesOf(string root) =>
        GetVolumeInformation(root, IntPtr.Zero, 0, out _, out _, out var flags, IntPtr.Zero, 0)
            ? (VolumeFeatures)flags
            : VolumeFeatures.None;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer,
        uint fileSystemNameSize);
}
