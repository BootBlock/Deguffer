using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the Arduino row may do to the folders one Arduino configuration names, and what must survive
/// it: whether <c>cache clean</c> may remove the downloads folder, whether the build cache may be read
/// as the CLI's, and which paths §5.6 asserts are still standing.
///
/// <para>Apart from <see cref="ArduinoCacheProvider"/>, which plans and measures, because these are the
/// row's §5.2 rules and each is asked by the plan, by Explore and by the folders a duplicate search
/// leaves out.</para>
/// </summary>
/// <param name="reach">The provider's own cached answer to where a folder is reached from.</param>
internal sealed class ArduinoFolderRules(
    IUserEnvironment environment,
    ISystemDirectories system,
    IVolumeInventory volumes,
    Func<string, ReachedFolder> reach)
{
    /// <summary>The folders the CLI stages archives in (the <c>CachePath</c> of each download resource).</summary>
    public static readonly DisposableChildSet StagingParts = new(
    [
        new ChildClassification("packages", SafetyTier.RegenerableCache, "Archives of boards and tools the Arduino CLI downloaded."),
        new ChildClassification("libraries", SafetyTier.RegenerableCache, "Archives of libraries the Arduino CLI downloaded."),
    ]);

    /// <summary>
    /// Why <c>cache clean</c> must not be run with these settings, as a sentence, or null where it may.
    /// Asked of what the settings name, before anything on the disk.
    /// </summary>
    public string? WhyNotCleared(ArduinoSettings settings)
    {
        if (settings.Downloads.Folder is not { } downloads)
        {
            return $"The Arduino CLI is set to keep its downloads in '{settings.Downloads.Reported}', which is not a "
                + "full path, so Deguffer cannot tell which folder its cache clean would remove.";
        }

        if (ConfiguredFolder.WhyNotOwned(
                downloads, environment, system, volumes, TempRoots.Resolve(environment, system, volumes).AccountFolders) is { } notOwned)
        {
            return $"The Arduino CLI is set to keep its downloads in '{downloads}', and Deguffer will not run its cache "
                + $"clean there: {notOwned} It is leaving it alone.";
        }

        return Overlapped(downloads, settings, settings.Downloads, stagingInData: true) is { } name
            ? $"The Arduino CLI is set to keep its downloads in '{downloads}', which is or holds its {name}, "
                + "and its cache clean removes that folder outright. Deguffer is leaving it alone."
            : null;
    }

    /// <summary>
    /// Why <c>cache clean</c>, sent to <paramref name="downloads"/>, must not run beside the folders
    /// <paramref name="startedNow"/> names, as a sentence, or null where it may. Those are the folders an
    /// Arduino CLI the user starts now uses, and the clean would remove whichever of them its folder is
    /// or holds.
    /// </summary>
    public string? WhyNotClearedBeside(string downloads, ArduinoSettings startedNow) =>
        Overlapped(downloads, startedNow, startedNow.Downloads, stagingInData: true) is { } name
            ? $"An Arduino CLI started now, with the settings changed since Deguffer started, keeps its {name} in or "
                + $"around '{downloads}', which the cache clean removes outright. Deguffer is leaving the downloads alone."
            : null;

    /// <summary>
    /// Why the build cache these settings name must not be read as the CLI's, as a sentence, or null
    /// where it may.
    /// </summary>
    public string? WhyNotRead(ArduinoSettings settings)
    {
        if (settings.BuildCache.Folder is not { } cache)
        {
            return $"The Arduino CLI is set to keep its build cache in '{settings.BuildCache.Reported}', which is not a "
                + "full path, so Deguffer cannot tell which folder it means.";
        }

        if (ConfiguredFolder.WhyNotOwned(
                cache, environment, system, volumes, TempRoots.Resolve(environment, system, volumes).AccountFolders) is { } notOwned)
        {
            return $"The Arduino CLI is set to keep its build cache in '{cache}', and Deguffer will not treat it as "
                + $"the CLI's: {notOwned} It is leaving it alone.";
        }

        return Overlapped(cache, settings, settings.BuildCache, stagingInData: false) is { } name
            ? $"The Arduino CLI is set to keep its build cache in '{cache}', which overlaps its {name}. "
                + "Deguffer cannot tell the CLI's builds from the rest, so it is leaving it alone."
            : null;
    }

    /// <summary>
    /// Why the build cache at <paramref name="cache"/> must not be read as the CLI's beside the folders
    /// <paramref name="startedNow"/> names, as a sentence, or null where it may.
    /// </summary>
    public string? WhyNotReadBeside(string cache, ArduinoSettings startedNow) =>
        Overlapped(cache, startedNow, startedNow.BuildCache, stagingInData: false) is { } name
            ? $"An Arduino CLI started now, with the settings changed since Deguffer started, keeps its {name} in or "
                + $"around the build cache '{cache}'. Deguffer cannot tell the CLI's builds from the rest, so it is leaving it alone."
            : null;

    /// <summary>
    /// Which folder <paramref name="settings"/> names, beside <paramref name="except"/>, that
    /// <paramref name="folder"/> is, holds or lies inside, or null where none.
    /// </summary>
    /// <param name="stagingInData">
    /// Whether <paramref name="folder"/> may lie inside the data folder as its own staging folder, which
    /// is where the CLI keeps its downloads by default.
    /// </param>
    private string? Overlapped(string folder, ArduinoSettings settings, ArduinoFolder except, bool stagingInData)
    {
        var reachedFolder = reach(folder);

        foreach (var (name, other) in Others(settings, except))
        {
            var reached = reach(other);

            if (reachedFolder.IsSameAs(reached)
                || reachedFolder.Holds(reached)
                || (reached.Holds(reachedFolder) && !(stagingInData && IsStagingOf(folder, settings))))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The other folders these settings name, beside <paramref name="except"/>, by what they are.</summary>
    private static IEnumerable<(string Name, string Folder)> Others(ArduinoSettings settings, ArduinoFolder except) =>
        new[]
        {
            ("data folder", settings.Data),
            ("downloads folder", settings.Downloads),
            ("build cache", settings.BuildCache),
            ("sketchbook", settings.Sketchbook),
        }
        .Where(other => !ReferenceEquals(other.Item2, except) && other.Item2.Folder is not null)
        .Select(other => (other.Item1, other.Item2.Folder!));

    /// <summary>Whether <paramref name="downloads"/> is the data folder's own staging folder, where the CLI puts it by default.</summary>
    public bool IsStagingOf(string downloads, ArduinoSettings settings) =>
        settings.Data.Folder is { } data
        && reach(downloads).IsSameAs(reach(Path.Combine(data, ArduinoSettings.DefaultDownloadsName)));

    /// <summary>
    /// Why the downloads folder as it stands on the disk must not be removed, or nothing where it may.
    /// <c>cache clean</c> removes every entry, and follows nothing, but a link at the folder itself is
    /// declined: what it points at was never classified.
    /// </summary>
    public static (bool Absent, bool Unlisted, string? Why) WhyNotEmptied(string downloads)
    {
        switch (LongPath.ProbeDirectory(downloads))
        {
            case PathPresence.Absent:
                return (true, false, null);
            case PathPresence.Refused:
                return (false, true, null);
        }

        if (LongPath.IsReparsePoint(downloads))
        {
            return (false, false, $"Leaving the Arduino downloads folder '{downloads}' alone: it is a link to somewhere "
                + "else, and the Arduino CLI's cache clean would remove what it points at.");
        }

        if (FolderEntries.Of(downloads) is not { } entries)
        {
            return (false, true, null);
        }

        foreach (var entry in entries)
        {
            if (entry is not DirectoryInfo directory
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !StagingParts.IsDisposable(directory.Name))
            {
                return (false, false, $"The Arduino downloads folder {downloads} holds '{entry.Name}', which the Arduino CLI "
                    + "does not put there, and its cache clean would remove it with everything else. Deguffer is leaving "
                    + "the downloads alone.");
            }
        }

        return (false, false, null);
    }

    /// <summary>
    /// §5.6. <c>cache clean</c> removes only the downloads folder, so the data folder and everything in
    /// it beside the downloads folders the plan removes must survive, and so must the sketchbook.
    /// </summary>
    /// <param name="removed">Every downloads folder a planned clean removes, which no entry here is.</param>
    public IEnumerable<(string Path, string Reason)> Survivors(ArduinoSettings settings, IReadOnlyList<string> removed)
    {
        if (settings.Data.Folder is { } data)
        {
            yield return (data, "The Arduino data folder, with your installed boards, tools and indexes.");

            foreach (var entry in FolderEntries.Of(data) ?? [])
            {
                var path = LongPath.Display(entry.FullName);

                if (!removed.Any(folder => reach(folder).IsSameAs(reach(path))))
                {
                    yield return (path, "Part of the Arduino data folder that is not downloaded archives.");
                }
            }
        }

        if (settings.Sketchbook.Folder is { } sketchbook)
        {
            yield return (sketchbook, "Your Arduino sketchbook, with your sketches and libraries.");
        }
    }
}
