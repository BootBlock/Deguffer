using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// One physical disk's class, or why it has none.
/// </summary>
internal readonly record struct DiskMedia(StorageMedia Class, MediaUnknownReason Reason = MediaUnknownReason.None, int Win32Error = 0)
{
    public static DiskMedia Unknown(MediaUnknownReason reason, int win32Error) =>
        new(StorageMedia.Unknown, reason, win32Error);
}

/// <summary>
/// Decides a volume's <see cref="StorageMedia"/> from what Windows says about it and the disks
/// behind it. Decides and does no I/O of its own: the queries come in through
/// <see cref="IStorageQueries"/>, and a disk's class through a function the cache answers once per
/// disk.
///
/// <para><b>No guess.</b> A query that fails or a descriptor that is too short gives
/// <see cref="StorageMedia.Unknown"/> with the step that did not answer. The one fallback taken is
/// from the bus to the seek penalty, and the seek penalty is an answer from the disk rather than a
/// guess about it.</para>
/// </summary>
internal static class MediaClassifier
{
    /// <summary>
    /// The class of the volume at <see cref="LocalVolume.RootPath"/>.
    ///
    /// <para>A share is network without asking, as the kind already says so and a share has no
    /// disk on this machine to ask. An optical drive is removable without asking, because it is
    /// not a disk and the extents query has nothing to say about it. A
    /// <see cref="System.IO.DriveType.Removable"/> volume is removable by its kind, and its disk is
    /// still asked for so that a caller can tell which volumes share it.</para>
    /// </summary>
    internal static VolumeMedia ClassifyVolume(LocalVolume volume, IStorageQueries queries, Func<int, DiskMedia> diskMedia)
    {
        switch (volume.Kind)
        {
            case DriveType.Network:
                return new VolumeMedia(StorageMedia.Network, []);

            case DriveType.CDRom:
                return new VolumeMedia(StorageMedia.Removable, []);

            case DriveType.Unknown or DriveType.NoRootDirectory:
                return new VolumeMedia(StorageMedia.Unknown, [], MediaUnknownReason.KindNotReported);
        }

        var extents = queries.DiskExtents(volume.RootPath);
        var removable = volume.Kind is DriveType.Removable;

        if (extents.Bytes is not { } bytes || !StorageDescriptors.TryReadDiskNumbers(bytes, out var disks))
        {
            return removable
                ? new VolumeMedia(StorageMedia.Removable, [])
                : new VolumeMedia(StorageMedia.Unknown, [], MediaUnknownReason.ExtentsNotRead, extents.Win32Error);
        }

        if (removable)
        {
            return new VolumeMedia(StorageMedia.Removable, disks);
        }

        if (disks.Count == 0)
        {
            return new VolumeMedia(StorageMedia.Unknown, disks, MediaUnknownReason.NoDiskBehindVolume);
        }

        var slowest = diskMedia(disks[0]);

        foreach (var disk in disks.Skip(1))
        {
            if (slowest.Class is StorageMedia.Unknown)
            {
                break;
            }

            slowest = Slower(slowest, diskMedia(disk));
        }

        return new VolumeMedia(slowest.Class, disks, slowest.Reason, slowest.Win32Error);
    }

    /// <summary>
    /// The class of <c>\\.\PhysicalDrive<paramref name="disk"/></c>.
    ///
    /// <para>The bus decides where it says something the seek penalty cannot: NVMe, a bridge to
    /// removable media, a disk reached over the network, a virtual disk. Every other bus, Storage
    /// Spaces included, is classed by whether the disk incurs a seek penalty, and is unknown where
    /// it does not say. An NVMe drive behind a RAID adapter reports the RAID bus and so is classed
    /// as solid state, which is the conservative side of the truth.</para>
    /// </summary>
    internal static DiskMedia ClassifyDisk(int disk, IStorageQueries queries)
    {
        var adapter = queries.Adapter(disk);

        if (adapter.Bytes is not { } adapterBytes || !StorageDescriptors.TryReadBusType(adapterBytes, out var bus))
        {
            return DiskMedia.Unknown(MediaUnknownReason.AdapterNotRead, adapter.Win32Error);
        }

        switch (bus)
        {
            case StorageBusType.Nvme:
                return new DiskMedia(StorageMedia.Nvme);

            case StorageBusType.Usb or StorageBusType.Sd or StorageBusType.Mmc or StorageBusType.Ieee1394:
                return new DiskMedia(StorageMedia.Removable);

            case StorageBusType.IScsi:
                return new DiskMedia(StorageMedia.Network);

            case StorageBusType.Virtual or StorageBusType.FileBackedVirtual:
                return new DiskMedia(StorageMedia.Virtual);
        }

        var seekPenalty = queries.SeekPenalty(disk);

        if (seekPenalty.Bytes is not { } seekBytes || !StorageDescriptors.TryReadSeekPenalty(seekBytes, out var incurs))
        {
            return DiskMedia.Unknown(MediaUnknownReason.SeekPenaltyNotRead, seekPenalty.Win32Error);
        }

        return new DiskMedia(incurs ? StorageMedia.Rotational : StorageMedia.SolidState);
    }

    /// <summary>
    /// The slower of two disks of one volume. A spanned or striped volume reads at the pace of its
    /// slowest disk, so that is the class a reader has to plan for.
    ///
    /// <para>An unknown disk makes the volume unknown, since the slowest disk is then not known.
    /// Speed is ordered only between rotational, solid state and NVMe. Two disks of the same kind
    /// agree whatever the kind, and any other pair has no slower member to name.</para>
    /// </summary>
    private static DiskMedia Slower(DiskMedia a, DiskMedia b)
    {
        if (b.Class is StorageMedia.Unknown)
        {
            return b;
        }

        if (a.Class == b.Class)
        {
            return a;
        }

        return (Speed(a.Class), Speed(b.Class)) switch
        {
            ({ } speedA, { } speedB) => speedA <= speedB ? a : b,
            _ => DiskMedia.Unknown(MediaUnknownReason.DisksOfUnrankedKinds, 0),
        };
    }

    private static int? Speed(StorageMedia media) => media switch
    {
        StorageMedia.Rotational => 0,
        StorageMedia.SolidState => 1,
        StorageMedia.Nvme => 2,
        _ => null,
    };
}
