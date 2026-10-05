using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A raw volume handle, opened by <see cref="VolumeMftSource.TryOpen(char, out FallbackReason)"/>
/// for overlapped I/O.
///
/// <para>Overlapped because Windows serialises the reads on a handle opened without it, however
/// many threads issue them, so only an overlapped handle can have several reads outstanding.
/// <see cref="RandomAccess"/> serves a synchronous read on it as well, by waiting for the
/// overlapped one.</para>
/// </summary>
internal sealed class VolumeHandle(SafeFileHandle handle) : IRawVolume
{
    public int Read(Span<byte> destination, long offset) => RandomAccess.Read(handle, destination, offset);

    public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken ct) =>
        RandomAccess.ReadAsync(handle, destination, offset, ct);

    public void Dispose() => handle.Dispose();
}
