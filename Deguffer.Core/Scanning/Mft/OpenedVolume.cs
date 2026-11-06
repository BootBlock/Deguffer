namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A raw volume whose boot sector <see cref="VolumeMftSource"/> has read, holding it until the table
/// is read or the volume is closed.
/// </summary>
internal sealed class OpenedVolume(IRawVolume volume, NtfsBootSector geometry) : IMftVolume
{
    // Taken by whichever comes first, the read of the table or the close, so the handle has exactly
    // one owner at every moment: this, then the source, or nobody once it is closed.
    private IRawVolume? _volume = volume;

    public IMftSource? TryReadTable(out FallbackReason reason) =>
        VolumeMftSource.TryReadTable(
            Interlocked.Exchange(ref _volume, null)
                ?? throw new InvalidOperationException("The table of this volume was read already, or the volume was closed."),
            geometry,
            out reason);

    public void Dispose() => Interlocked.Exchange(ref _volume, null)?.Dispose();
}
