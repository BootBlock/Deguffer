using Deguffer.Core.ModManagers;

namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What Explore says about each mod manager download folder this account's records lead to: whose
/// archives they are, and the manager's own way to remove them.
///
/// <para>Discovered rather than catalogued, because a Mod Organizer 2 instance's folder is wherever its
/// settings put it. The text comes from <see cref="ModManagerRoutes"/>, which the report and Explore's
/// refusal read too, so the three cannot disagree.</para>
/// </summary>
internal static class ModManagerItems
{
    public static IReadOnlyList<KnownItem> For(ModManagerInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return
        [
            .. inventory.VortexFound.Select(folder => new KnownItem(
                KnownPlace.Discovered,
                folder.Folder,
                ModManagerRoutes.VortexSummary(folder),
                ModManagerRoutes.VortexRemoval)),
            .. inventory.ModOrganizerDownloads
                .DistinctBy(entry => entry.Downloads, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new KnownItem(
                    KnownPlace.Discovered,
                    entry.Downloads,
                    ModManagerRoutes.ModOrganizerSummary(entry.Instance),
                    ModManagerRoutes.ModOrganizerRemoval)),
        ];
    }
}
