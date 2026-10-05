namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A volume read by byte offset, the way <see cref="VolumeMftSource"/> reads one.
///
/// <para>The seam that lets the source be tested at all. Opening a real volume needs administrator
/// rights (§6.3), and a disk with 512-byte sectors accepts reads that one with 4,096-byte sectors
/// refuses, so a test reads a synthesised image that refuses them as the larger disk does.</para>
/// </summary>
internal interface IRawVolume : IDisposable
{
    /// <summary>
    /// Read into <paramref name="destination"/> from <paramref name="offset"/>, returning how many
    /// bytes arrived. Throws <see cref="IOException"/> where the volume refuses the read, which a
    /// read that is not whole sectors is.
    /// </summary>
    int Read(Span<byte> destination, long offset);

    /// <summary>
    /// <see cref="Read"/>, overlapped: the read is in flight when this returns, and holds no thread
    /// while the disk works. The same rules apply, and a read the volume refuses faults the task with
    /// an <see cref="IOException"/>.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken ct);
}
