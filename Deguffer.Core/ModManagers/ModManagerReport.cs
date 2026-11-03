using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.ModManagers;

/// <summary>What the measurement said about one folder the report names.</summary>
/// <param name="Size">What the folder holds.</param>
/// <param name="Reached">False where the measurement could not reach it, so <paramref name="Size"/> describes nothing.</param>
internal readonly record struct MeasuredFolder(ScanSize Size, bool Reached);

/// <summary>
/// The Storage page's report on the mod managers' download folders, as the plan's notes: each folder's
/// size, Vortex's split by game, Mod Organizer 2's split by what its records mark installed and
/// uninstalled, and the managers' own routes.
/// </summary>
internal static class ModManagerReport
{
    /// <param name="inventory">What the managers' folders and records said.</param>
    /// <param name="measured">What each folder measured, keyed by its path. A folder missing here was not there to measure.</param>
    /// <param name="archives">What each Mod Organizer 2 download folder's records say, keyed by its path.</param>
    /// <param name="handlerRecord">Where the record that leads to a portable Mod Organizer 2 is.</param>
    public static IReadOnlyList<PlanNote> Notes(
        ModManagerInventory inventory,
        IReadOnlyDictionary<string, MeasuredFolder> measured,
        IReadOnlyDictionary<string, ModOrganizerArchives> archives,
        string handlerRecord)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(archives);

        var notes = new List<PlanNote>();

        // Each folder once: two instances can share one download folder.
        var reached = inventory.VortexFound.Select(folder => folder.Folder)
            .Concat(inventory.ModOrganizerDownloads.Select(entry => entry.Downloads))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(folder => measured.TryGetValue(folder, out var size) && size.Reached)
            .ToList();

        if (reached.Count > 0)
        {
            var total = reached.Aggregate(ScanSize.Zero, (sum, folder) => sum + measured[folder].Size);

            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Mod managers keep {FreeSpace.Format(total)} of downloaded mod archives in "
                + $"{(reached.Count == 1 ? "1 folder" : $"{reached.Count} folders")}."));
        }

        foreach (var folder in inventory.VortexFound)
        {
            notes.Add(Vortex(folder, measured));
        }

        var described = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in inventory.ModOrganizer.Instances)
        {
            notes.Add(ModOrganizer(instance, measured, archives, described));
        }

        notes.AddRange(inventory.ModOrganizer.Problems.Select(problem => new PlanNote(PlanNoteSeverity.Warning, problem)));

        if (inventory.ModOrganizer.Instances.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, ModManagerRoutes.ModOrganizerRoute));
        }

        if (inventory.VortexFound.Any())
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, ModManagerRoutes.VortexRoute));
        }

        notes.Add(new PlanNote(PlanNoteSeverity.Information, ModManagerRoutes.WhyNothingIsOffered));
        notes.Add(new PlanNote(PlanNoteSeverity.Information, ModManagerRoutes.VortexMovedFolder));
        notes.Add(new PlanNote(PlanNoteSeverity.Information, ModManagerRoutes.PortableModOrganizer(handlerRecord)));

        return notes;
    }

    private static PlanNote Vortex(VortexDownloadFolder folder, IReadOnlyDictionary<string, MeasuredFolder> measured)
    {
        if (!measured.TryGetValue(folder.Folder, out var whole) || !whole.Reached)
        {
            return UnreadableRoot.UnmeasuredNote(folder.Folder);
        }

        var games = folder.Games
            .Select(game => (Name: Path.GetFileName(game), Size: measured.TryGetValue(game, out var size) ? size : default))
            .Where(game => game.Size.Reached && game.Size.Size.Logical > 0)
            .OrderByDescending(game => game.Size.Size.Logical)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Select(game => $"{game.Name} {FreeSpace.Format(game.Size.Size)}")
            .ToList();

        var unlisted = folder.Listed ? string.Empty : " Windows would not list the folder, so its games are not named.";
        var byGame = games.Count > 0 ? $", by game: {string.Join(", ", games)}." : ".";

        return new PlanNote(
            folder.Listed ? PlanNoteSeverity.Information : PlanNoteSeverity.Warning,
            $"{ModManagerRoutes.Capitalised(folder.Whose)}, '{folder.Folder}', take {FreeSpace.Format(whole.Size)}{byGame}"
            + unlisted);
    }

    private static PlanNote ModOrganizer(
        ModOrganizerInstance instance,
        IReadOnlyDictionary<string, MeasuredFolder> measured,
        IReadOnlyDictionary<string, ModOrganizerArchives> archives,
        HashSet<string> described)
    {
        var whose = ModManagerRoutes.Capitalised(instance.Whose);

        if (instance.Downloads is not { } downloads)
        {
            var named = instance.Named is null ? "name a download folder" : $"name '{instance.Named}' for its downloads";

            return new PlanNote(
                PlanNoteSeverity.Information,
                $"{whose}: its settings {named}, which Deguffer does not measure as the instance's: {instance.Declined}");
        }

        if (!measured.TryGetValue(downloads, out var size))
        {
            return new PlanNote(
                PlanNoteSeverity.Information,
                $"{whose}: its settings name '{downloads}' for its downloads, and there is no folder there.");
        }

        if (!size.Reached)
        {
            return UnreadableRoot.UnmeasuredNote(downloads);
        }

        if (!described.Add(downloads))
        {
            return new PlanNote(
                PlanNoteSeverity.Information,
                $"{whose} keeps its downloads in '{downloads}' too, which is counted once.");
        }

        var split = archives.TryGetValue(downloads, out var state) && state.Listed
            ? $" Of that, {FreeSpace.Format(state.Installed)} is archives Mod Organizer 2 marks as installed, and "
                + $"{FreeSpace.Format(state.Uninstalled)} archives it marks as uninstalled."
            : " Windows would not list the folder, so how much of it is installed is not known.";

        return new PlanNote(
            PlanNoteSeverity.Information,
            $"{whose}: its downloads, '{downloads}', take {FreeSpace.Format(size.Size)}.{split}");
    }
}
