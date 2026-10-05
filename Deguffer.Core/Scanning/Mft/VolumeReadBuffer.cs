using System.Buffers;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Memory a raw volume read can land in directly.
///
/// <para>Windows treats a volume handle as unbuffered, and Microsoft's rules for an unbuffered read
/// ask for a buffer address aligned to the physical sector size, which a managed array does not
/// promise. Some disks do not enforce it, so a 512-byte disk can pass with a managed array while a
/// 4,096-byte one refuses, and a refused read sends the whole volume to the walk. Aligning to
/// <see cref="Alignment"/> satisfies every sector size <see cref="NtfsBootSector"/> accepts. It
/// also satisfies the storage adapter's own requirement, which <c>ArrayPool</c> does not promise
/// either: the <c>STORAGE_ADAPTER_DESCRIPTOR</c> documentation lists 0, 1, 3 and 7 as the valid
/// values of <c>AlignmentMask</c>, so no adapter asks for more than eight bytes.</para>
///
/// <para>A <see cref="MemoryManager{T}"/> so an overlapped read can be handed its
/// <see cref="Memory"/>. The memory is unmanaged, so it never moves and pinning it costs nothing.
/// It is released by <see cref="IDisposable.Dispose"/> and by nothing else, and a caller disposes it
/// only once every read into it has completed.</para>
/// </summary>
internal sealed unsafe class VolumeReadBuffer : MemoryManager<byte>
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

    public Span<byte> Span => GetSpan();

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_memory is null, this);
        return new Span<byte>(_memory, Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_memory is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, Length);

        return new MemoryHandle((byte*)_memory + elementIndex);
    }

    /// <summary>Nothing to release: the memory never moves, so <see cref="Pin"/> pinned nothing.</summary>
    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        NativeMemory.AlignedFree(_memory);
        _memory = null;
    }
}
