using Deguffer.Core.Safety;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// What Vortex's default download folders hold and which Mod Organizer 2 instances this account's
/// records lead to, read once and shared by the report, Explore's refusals and Explore's descriptions.
/// </summary>
/// <param name="Vortex">Each of Vortex's default download folders that may be there.</param>
/// <param name="ModOrganizer">The Mod Organizer 2 instances, and what stopped one being read.</param>
internal sealed record ModManagerInventory(IReadOnlyList<VortexDownloadFolder> Vortex, ModOrganizerReading ModOrganizer)
{
    private IReadOnlyList<(ModOrganizerInstance Instance, string Downloads)>? _modOrganizerDownloads;

    public static ModManagerInventory Read(IUserEnvironment environment, ISystemDirectories system, IVolumeInventory volumes) =>
        new(
            [.. VortexDownloads.Read(environment, system).Where(folder => folder.Presence != PathPresence.Absent)],
            ModOrganizerInstances.Read(environment, system, volumes));

    /// <summary>Each Mod Organizer 2 download folder measured as an instance's, with the instance.</summary>
    public IReadOnlyList<(ModOrganizerInstance Instance, string Downloads)> ModOrganizerDownloads =>
        _modOrganizerDownloads ??=
        [
            .. ModOrganizer.Instances.SelectMany(instance => instance.Downloads is { } downloads
                ? [(instance, downloads)]
                : Array.Empty<(ModOrganizerInstance, string)>()),
        ];
}
