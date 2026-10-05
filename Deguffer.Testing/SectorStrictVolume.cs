using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// A volume image that refuses a read as an unbuffered volume handle does: unless it starts on a
/// sector boundary, runs a whole number of sectors, and lands in memory aligned to a sector.
///
/// <para>The refusal is the point. A disk with 512-byte sectors serves almost any read a reader
/// makes, so a reader that gets the 4,096-byte case wrong passes against a lenient image and fails
/// on the disk. Windows refuses such a read with "The parameter is incorrect", which .NET raises as
/// an <see cref="IOException"/>.</para>
/// </summary>
internal sealed class SectorStrictVolume(byte[] image, int bytesPerSector) : IRawVolume
{
    public int Read(Span<byte> destination, long offset)
    {
        if (offset % bytesPerSector != 0
            || destination.Length % bytesPerSector != 0
            || Address(destination) % bytesPerSector != 0)
        {
            throw new IOException("The parameter is incorrect.");
        }

        if (offset >= image.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(destination.Length, image.Length - offset);
        image.AsSpan((int)offset, count).CopyTo(destination);
        return count;
    }

    public void Dispose()
    {
    }

    private static long Address(Span<byte> span) =>
        Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref MemoryMarshal.GetReference(span));
}
