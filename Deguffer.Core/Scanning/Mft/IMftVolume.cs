namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A volume opened for its table, whose boot sector says the table can be read, and whose table has
/// not been read yet.
///
/// <para><b>Why the open and the table are two steps.</b> NTFS serves the first read through a
/// volume handle that reaches past the volume's first 8 KiB by flushing and purging what it has
/// cached for every file on that volume, so the read sees the disk as the cache does. A read of the
/// boot sector alone does not. Measured on one NVMe volume right after a walk of a source folder on
/// it, that first read took 0.8 to 1.6 s, against under 1 ms for the boot sector, and the next walk
/// of the volume then waited on a checkpoint of the file system's log. Later reads through the same
/// handle cost nothing more (#544).</para>
///
/// <para>So the open, which says whether the table can be read at all, can run where a caller must
/// know that at once, and the read that pays for the flush can run beside the walk it is raced
/// against (<see cref="MftVolumeIndexCache"/>).</para>
///
/// <para>The volume is closed by <see cref="IDisposable.Dispose"/> until
/// <see cref="TryReadTable"/> has been called, and from then on by the source it returned, or
/// already, where it returned none.</para>
/// </summary>
public interface IMftVolume : IDisposable
{
    /// <summary>
    /// Read where the volume keeps its table, and hand the volume to the source that reads it. Null
    /// with the reason where the table cannot be found, with the volume closed. Called once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The table was read already, or the volume closed.</exception>
    IMftSource? TryReadTable(out FallbackReason reason);

    /// <summary>
    /// Serve the reads of this volume's table at <paramref name="priority"/> from now on, including
    /// those of the source <see cref="TryReadTable"/> handed it to. A hint, and nothing once the
    /// volume or its source is closed. A volume reads at <see cref="ReadPriority.Normal"/> until this
    /// is called.
    /// </summary>
    void Prioritise(ReadPriority priority);
}
