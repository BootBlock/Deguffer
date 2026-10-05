using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>A raw volume handle, opened by <see cref="VolumeMftSource.TryOpen"/>.</summary>
internal sealed class VolumeHandle(SafeFileHandle handle) : IRawVolume
{
    public int Read(Span<byte> destination, long offset) => RandomAccess.Read(handle, destination, offset);

    public void Dispose() => handle.Dispose();
}
