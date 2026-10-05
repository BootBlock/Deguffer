using System.Runtime.InteropServices;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Memory a raw volume read can land in directly.
///
/// <para>Windows treats a volume handle as unbuffered, and Microsoft's rules for an unbuffered read
/// ask for a buffer address aligned to the physical sector size, which a managed array does not
/// promise. Some disks do not enforce it, so a 512-byte disk can pass with a managed array while a
/// 4,096-byte one refuses, and a refused read sends the whole volume to the walk. Aligning to
/// <see cref="Alignment"/> satisfies every sector size <see cref="NtfsBootSector"/> accepts.</para>
///
/// <para>Unmanaged, so it is released by <see cref="Dispose"/> and by nothing else.</para>
/// </summary>
internal sealed unsafe class VolumeReadBuffer : IDisposable
{
    public const int Alignment = NtfsBootSector.MaximumBytesPerSector;

    private void* _memory;

    public VolumeReadBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        Length = length;
        _memory = NativeMemory.AlignedAlloc((nuint)length, Alignment);
    }

    public int Length { get; }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_memory is null, this);
            return new Span<byte>(_memory, Length);
        }
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_memory);
        _memory = null;
    }
}
