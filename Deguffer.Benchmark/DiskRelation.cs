using Deguffer.Core.Scanning.Media;

namespace Deguffer.Benchmark;

/// <summary>
/// Where the race's build reads, against where its walk lists: what separates a cost the drive
/// imposes from one the process imposes on itself. A build of another disk shares nothing with the
/// walk but the process.
/// </summary>
internal enum DiskRelation
{
    /// <summary>The drives did not say which disks hold them.</summary>
    Unknown,

    /// <summary>The build is of the walked folder's own volume.</summary>
    SameVolume,

    /// <summary>The build is of another volume on a disk the walked folder's volume is on too.</summary>
    SameDisk,

    /// <summary>The build is of a volume on no disk the walked folder's volume is on.</summary>
    OtherDisk,
}

internal static class DiskRelations
{
    /// <param name="walked">What the walked folder's volume is on, or null where nothing was asked.</param>
    /// <param name="built">What the built volume is on, or null where nothing was asked.</param>
    public static DiskRelation Of(char walkedDrive, VolumeMedia? walked, char builtDrive, VolumeMedia? built)
    {
        if (char.ToUpperInvariant(walkedDrive) == char.ToUpperInvariant(builtDrive))
        {
            return DiskRelation.SameVolume;
        }

        if (walked is not { PhysicalDisks.Count: > 0 } a || built is not { PhysicalDisks.Count: > 0 } b)
        {
            return DiskRelation.Unknown;
        }

        return a.PhysicalDisks.Intersect(b.PhysicalDisks).Any() ? DiskRelation.SameDisk : DiskRelation.OtherDisk;
    }

    public static string Describe(this DiskRelation relation) => relation switch
    {
        DiskRelation.SameVolume => "the folder's own volume",
        DiskRelation.SameDisk => "another volume on the folder's disk",
        DiskRelation.OtherDisk => "a volume on another disk",
        _ => "a volume on disks it did not name",
    };
}
