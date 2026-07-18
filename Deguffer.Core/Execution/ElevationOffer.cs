using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether the preview should offer to relaunch elevated.
///
/// §6.3 runs the app unelevated, so <see cref="FallbackReason.NotElevated"/> is the ordinary
/// outcome and the only one elevating can fix. A non-NTFS volume or an unaddressable path takes the
/// walk no matter who is asking, and offering administrator rights there would be a lie.
/// </summary>
public static class ElevationOffer
{
    public static bool ShouldOffer(bool isElevated, IEnumerable<Finding> findings) =>
        !isElevated && findings.Any(f => f.Plan?.Fallback is FallbackReason.NotElevated);
}
