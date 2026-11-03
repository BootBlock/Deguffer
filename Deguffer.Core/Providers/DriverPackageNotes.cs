using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <see cref="DriverStoreProvider"/>'s plan tells the user about driver packages: the notes on
/// what it did not consider or offer, and how it names a package and a count of them.
/// </summary>
internal static class DriverPackageNotes
{
    /// <summary>Why a folder stays where the plan names exactly what <c>pnputil</c> removes.</summary>
    public const string NotAsked = "A driver package pnputil was not asked to remove.";

    /// <summary>
    /// Said where Windows' cleanup does the work and a listed package could not be matched to its
    /// folder: that cleanup may rightly take the unmatched package, so no unlisted folder can be
    /// asserted afterwards. Where <c>pnputil</c> does the work, Deguffer names exactly what goes and
    /// every unlisted folder is asserted regardless.
    /// </summary>
    public static readonly PlanNote Unmatched = new(
        PlanNoteSeverity.Information,
        "Deguffer could not match every driver package Windows listed to its folder, so it cannot tell "
        + "which folders in the store hold drivers that are part of Windows, and the clean cannot check "
        + "afterwards that they are still there.");

    /// <summary>What the plan tells the user about the packages it did not consider or will not offer.</summary>
    public static IEnumerable<PlanNote> Considered(DriverStoreListing listing, SupersededDrivers drivers)
    {
        if (listing.Unread > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Deguffer could not read what Windows listed about {Count(listing.Unread, "driver package")}, "
                + "so none of them is offered.");
        }

        if (drivers.InUse.Count > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {Count(drivers.InUse.Count, "older package")} alone although a newer version is "
                + (drivers.InUse.Count == 1
                    ? "here: a device is installed with it."
                    : "here: a device is installed with each of them."));
        }

        if (drivers.Unplaced > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving {Count(drivers.Unplaced, "older package")} alone: Windows did not say where in its "
                + "driver store they are.");
        }
    }

    public static string Described(DriverPackage package) =>
        $"{package.Provider}'s {package.OriginalName} version {package.Version} of {package.Date:d MMMM yyyy}";

    public static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
}
