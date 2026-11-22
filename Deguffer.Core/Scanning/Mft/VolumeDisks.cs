namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A volume a walk or an index build reads, by its root, and the physical disks under it, for
/// <see cref="BuildPriorities"/>.
/// </summary>
/// <param name="Root">The volume's root, or null where no volume of this machine could be named for it.</param>
/// <param name="Disks">The physical disks under the volume, or null where it did not say which.</param>
internal readonly record struct VolumeDisks(string? Root, IReadOnlyList<int>? Disks)
{
    /// <summary>A volume nothing could be learned about, which is taken to be on every disk and to be no build's own.</summary>
    public static VolumeDisks Unknown => default;

    /// <summary>
    /// Whether a walk of this volume is one a build of <paramref name="built"/> yields to: a walk of
    /// another volume on one of its disks, or of a volume or on disks either side could not name.
    ///
    /// <para>A walk of the build's own volume is not: a read in flight there was measured to change
    /// nothing, warm or cold (#477), because what slows the walk is the flush NTFS makes at the
    /// table's first read (#544), which priority does not touch. See <see cref="BuildPriorities"/>.</para>
    /// </summary>
    public bool Contends(VolumeDisks built)
    {
        if (SameVolume(built))
        {
            return false;
        }

        if (Disks is null || built.Disks is null)
        {
            return true;
        }

        foreach (var disk in Disks)
        {
            if (built.Disks.Contains(disk))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether both are named, and are one volume. A volume with no root named is never the same as another.</summary>
    public bool SameVolume(VolumeDisks other) =>
        Root is not null && other.Root is not null && Root.Equals(other.Root, StringComparison.OrdinalIgnoreCase);
}
