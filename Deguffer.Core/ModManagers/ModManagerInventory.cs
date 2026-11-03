using Deguffer.Core.Safety;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// What Vortex's default download folders hold and which Mod Organizer 2 instances this account's
/// records lead to, read once and shared by the report, Explore's refusals and Explore's descriptions.
/// </summary>
internal sealed record ModManagerInventory(IReadOnlyList<VortexDownloadFolder> Vortex, ModOrganizerReading ModOrganizer)
{
    public static ModManagerInventory Read(IUserEnvironment environment, ISystemDirectories system, IVolumeInventory volumes) =>
        new(VortexDownloads.Read(environment, system), ModOrganizerInstances.Read(environment, system, volumes));

    /// <summary>Each Vortex download folder that may be there.</summary>
    public IEnumerable<VortexDownloadFolder> VortexFound => Vortex.Where(folder => folder.Presence != PathPresence.Absent);

    /// <summary>Each Mod Organizer 2 download folder measured as an instance's.</summary>
    public IEnumerable<(ModOrganizerInstance Instance, string Downloads)> ModOrganizerDownloads =>
        ModOrganizer.Instances
            .Where(instance => instance.Downloads is not null)
            .Select(instance => (instance, instance.Downloads!));
}
