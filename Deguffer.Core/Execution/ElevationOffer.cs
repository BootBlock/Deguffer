using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether the preview should offer to relaunch elevated.
///
/// §6.3 runs the app unelevated, so this is the ordinary posture rather than an edge case. Two
/// unrelated things are improved by elevating, and both are read here:
///
/// <list type="bullet">
/// <item><see cref="FallbackReason.NotElevated"/> — a size that had to be walked for rather than
/// read from the file table. The number is right, and the offer is not a promise of a quicker run:
/// the table answers a location without walking it, and building it costs one pass over the volume,
/// which on a machine with several volumes and a modest source tree came to more than it saved. See
/// <see cref="Deguffer.App.Views.AboutPage"/>'s scan-mode note for what was measured.</item>
/// <item><see cref="CleanupPlan.RequiresElevation"/> — a step that cannot be carried out at all,
/// whatever route measured it. The Windows directory and <c>%PROGRAMDATA%</c> are where this
/// arises.</item>
/// </list>
///
/// A non-NTFS volume or an unaddressable path takes the walk no matter who is asking, and offering
/// administrator rights for that alone would be a lie — but such a plan may still hold a step that
/// needs them, which is why the two conditions are independent rather than nested.
/// </summary>
public static class ElevationOffer
{
    public static bool ShouldOffer(bool isElevated, IEnumerable<Finding> findings) =>
        !isElevated && findings.Any(f =>
            f.Plan?.Fallback is FallbackReason.NotElevated || f.Plan?.RequiresElevation is true);
}
