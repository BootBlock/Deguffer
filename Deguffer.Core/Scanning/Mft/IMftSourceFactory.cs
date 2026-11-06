namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Opens the MFT of a volume, or says why it could not.
///
/// The seam exists so the scanner's route selection — which volumes take the fast path, what
/// happens when one cannot, how failures are cached — is testable without administrator rights on
/// the build agent (§6.3, G8).
///
/// <para>In two steps, <see cref="TryOpen"/> and then <see cref="IMftVolume.TryReadTable"/>,
/// because the second costs the whole volume and the first costs nothing: see
/// <see cref="IMftVolume"/>.</para>
/// </summary>
public interface IMftSourceFactory
{
    /// <summary>
    /// Open the volume holding <paramref name="driveLetter"/> and say whether its table can be read,
    /// without reading it. Null with the reason where it cannot: not elevated, not addressable, or
    /// not NTFS.
    /// </summary>
    IMftVolume? TryOpen(char driveLetter, out FallbackReason reason);

    /// <summary>
    /// <see cref="TryOpen"/> and <see cref="IMftVolume.TryReadTable"/> together, for a caller that
    /// reads the table as soon as it has opened the volume.
    /// </summary>
    public IMftSource? TryOpenTable(char driveLetter, out FallbackReason reason)
    {
        using var volume = TryOpen(driveLetter, out reason);

        return volume?.TryReadTable(out reason);
    }
}

/// <inheritdoc />
public sealed class VolumeMftSourceFactory : IMftSourceFactory
{
    public static readonly VolumeMftSourceFactory Default = new();

    public IMftVolume? TryOpen(char driveLetter, out FallbackReason reason) =>
        VolumeMftSource.TryOpen(driveLetter, out reason);
}
