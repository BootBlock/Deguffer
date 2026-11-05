using Deguffer.Core.Execution;
using Deguffer.Core.ModManagers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The archives Vortex and Mod Organizer 2 keep after installing each mod, reported and never offered.
///
/// <para><b>Why nothing is offered.</b> An archive looks as if it downloads again, and often does not: a
/// file Nexus Mods archives cannot be downloaded directly, and one its staff remove is gone. Reinstalling
/// a mod and installing a collection read these archives too, and nothing on the machine says which are
/// still available. So the folders are Tier 3, and the row names each manager's own deletion (§5.1)
/// rather than offering one of its own.</para>
///
/// <para><b>Found from the managers' own defaults and records.</b> Vortex's default download folder, for
/// one account and for its shared mode, and every Mod Organizer 2 instance its instance manager would
/// list or its download handler names, at the download folder the instance's settings give. Vortex
/// keeps a moved download folder in its own database, which is not read, and the report says so.</para>
///
/// <para><b>Never reported, and never offered by any row:</b> Vortex's staging folders, which are the
/// installed mods themselves and which hard-link deployment shares with the game, and Mod Organizer 2's
/// <c>mods</c>, <c>profiles</c> and <c>overwrite</c>.</para>
///
/// <para><b>Explore refuses each download folder</b> and everything in it, with the manager's route, and
/// describes it on hover from the same text.</para>
/// </summary>
public sealed class ModManagerDownloadProvider : CleanupProviderBase
{
    private readonly ISystemDirectories _system;

    /// <summary>What the managers record, read once per planning pass and shared by presence, planning and Explore (G4).</summary>
    private readonly PassMemo<ModManagerInventory> _inventory = new();

    public ModManagerDownloadProvider(
        ISystemDirectories? system = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "mod-manager-downloads";

    public override string Name => "Vortex and Mod Organizer 2 downloads";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Nothing changes: Deguffer only reports these downloads. Removing them is done inside Vortex or Mod "
        + "Organizer 2, which the report names.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Games,
        Application = "Vortex and Mod Organizer 2, the game mod managers",
        Publisher = "Nexus Mods, and the Mod Organizer 2 team",
        Purpose = "Both mod managers keep every mod archive they download after installing it, to reinstall the "
            + "mod or install a collection later. On a machine that mods large games these folders reach tens of "
            + "gigabytes for each game.",
        Recommendation = "Deguffer reports each download folder's size, by game for Vortex and by what Mod "
            + "Organizer 2 marks installed or uninstalled, and names each manager's own way to remove them. It "
            + "offers none of them, because an archive may not be available to download again. The installed "
            + "mods, Vortex's staging folders and Mod Organizer 2's mods, profiles and overwrite folders are never "
            + "touched.",
    };

    public override void InvalidateCaches()
    {
        _inventory.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Each download folder, refused with everything in it and with the manager's route, which Explore
    /// states for an entry anywhere below. Vortex's default folders are fixed, so they are refused whether
    /// or not they are there: a folder Vortex makes after Explore built its policy is still Vortex's.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            Refused(VortexDownloads.Default(Environment), ModManagerRoutes.VortexRefusal(VortexDownloads.Whose(shared: false))),
            Refused(VortexDownloads.SharedDefault(_system), ModManagerRoutes.VortexRefusal(VortexDownloads.Whose(shared: true))),
            .. Inventory().ModOrganizerDownloads
                .DistinctBy(entry => entry.Downloads, StringComparer.OrdinalIgnoreCase)
                .Select(entry => Refused(entry.Downloads, ModManagerRoutes.ModOrganizerRefusal(entry.Instance))),
        ]);

    /// <summary>A Vortex download folder, a Mod Organizer 2 instance, or a record that could not be read.</summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var inventory = Inventory();

        return Task.FromResult(
            inventory.Vortex.Any()
            || inventory.ModOrganizer.Instances.Count > 0
            || inventory.ModOrganizer.Problems.Count > 0);
    }

    /// <summary>None: the row only reports, and its plan holds no step.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var inventory = Inventory();

        // Each Vortex folder with its games, read off one walk, and each instance's folder once.
        List<string> paths =
        [
            .. inventory.Vortex
                .Where(folder => folder.Presence == PathPresence.Present)
                .SelectMany(folder => folder.Games.Prepend(folder.Folder)),
        ];

        var instanceFolders = inventory.ModOrganizerDownloads
            .Select(entry => entry.Downloads)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(folder => LongPath.ProbeDirectory(folder) != PathPresence.Absent)
            .ToList();

        paths.AddRange(instanceFolders.Where(folder => !paths.Contains(folder, StringComparer.OrdinalIgnoreCase)));

        // The records need nothing from the measurement, so they are read while it runs. Where the
        // measurement walks, both list the same folders, and whichever lists one first leaves it warm.
        var reading = ModOrganizerArchives.OfAllAsync(instanceFolders, ct);
        var measuring = MeasureAsync();

        // Both awaited together, so neither one's failure leaves the other unobserved.
        await Task.WhenAll(reading, measuring).ConfigureAwait(false);

        var archives = await reading.ConfigureAwait(false);
        var batch = await measuring.ConfigureAwait(false);

        var measured = new Dictionary<string, MeasuredFolder>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < paths.Count; i++)
        {
            measured[paths[i]] = new MeasuredFolder(batch!.Sizes[i], batch.Reached[i]);
        }

        List<PlanNote> notes =
        [
            .. ModManagerReport.Notes(
                inventory,
                measured,
                archives,
                Path.Combine(ModOrganizerInstances.Root(Environment), ModOrganizerInstances.HandlerRecordName)),
        ];

        if (batch?.Note is { } route)
        {
            notes.Add(route);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            ReportsOnly = true,
            Notes = notes,
        };

        async Task<ScanBatch?> MeasureAsync() =>
            paths.Count > 0 ? await MeasureAllAsync(paths, ct).ConfigureAwait(false) : null;
    }

    /// <summary>A folder nothing inside may go from, which is a root that recognises no child.</summary>
    private static ToolRoot Refused(string folder, string reason) => new(folder, reason, static _ => false);

    private ModManagerInventory Inventory() => _inventory.Get(() => ModManagerInventory.Read(Environment, _system, Volumes));
}
