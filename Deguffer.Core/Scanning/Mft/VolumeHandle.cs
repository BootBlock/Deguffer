using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A raw volume handle, opened by <see cref="VolumeMftSource.TryOpen(char, out FallbackReason, MftOpenTimings?)"/>
/// for overlapped I/O.
///
/// <para>Overlapped because Windows serialises the reads on a handle opened without it, however
/// many threads issue them, so only an overlapped handle can have several reads outstanding.
/// <see cref="RandomAccess"/> serves a synchronous read on it as well, by waiting for the
/// overlapped one.</para>
/// </summary>
internal sealed partial class VolumeHandle(SafeFileHandle handle) : IRawVolume
{
    public int Read(Span<byte> destination, long offset) => RandomAccess.Read(handle, destination, offset);

    public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken ct) =>
        RandomAccess.ReadAsync(handle, destination, offset, ct);

    /// <summary>
    /// Set the handle's I/O priority hint, which the I/O manager stamps on every request the handle
    /// issues from then on. A disk trace of 1 MiB reads through a raw volume handle on NVMe showed each
    /// read reach the disk at the priority set here (#478).
    /// </summary>
    public void Prioritise(ReadPriority priority)
    {
        var hint = priority is ReadPriority.Low ? IoPriorityHintLow : IoPriorityHintNormal;

        try
        {
            // The result is not checked. A driver that refuses the hint serves the reads at normal
            // priority, which is what every read had before it was asked, so there is nothing to undo.
            _ = SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref hint, sizeof(int));
        }
        catch (ObjectDisposedException)
        {
            // The build finished and closed the handle before the caller raised its priority. Its
            // reads are over, so there is nothing left to prioritise.
        }
    }

    public void Dispose() => handle.Dispose();

    private const int FileIoPriorityHintInfo = 12;
    private const int IoPriorityHintLow = 1;
    private const int IoPriorityHintNormal = 2;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref int information, int size);
}
