namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Opens the MFT of a volume, or says why it could not.
///
/// The seam exists so the scanner's route selection — which volumes take the fast path, what
/// happens when one cannot, how failures are cached — is testable without administrator rights on
/// the build agent (§6.3, G8).
/// </summary>
public interface IMftSourceFactory
{
    IMftSource? TryOpen(char driveLetter, out FallbackReason reason);
}

/// <inheritdoc />
public sealed class VolumeMftSourceFactory : IMftSourceFactory
{
    public static readonly VolumeMftSourceFactory Default = new();

    public IMftSource? TryOpen(char driveLetter, out FallbackReason reason) =>
        VolumeMftSource.TryOpen(driveLetter, out reason);
}
