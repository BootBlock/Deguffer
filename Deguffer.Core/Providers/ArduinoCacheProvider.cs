using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Arduino IDE 2 and <c>arduino-cli</c> keep that they can make again: the archives of boards,
/// tools and libraries they downloaded, and the cores and sketches they compiled.
///
/// <para><b>Researched from source, not measured.</b> No machine this was written against has Arduino
/// installed. Every location and rule below cites the arduino-cli or arduino-ide source it comes
/// from.</para>
///
/// <para><b>Downloaded archives, §5.1 with a command that removes whatever it is pointed at.</b>
/// <c>arduino-cli cache clean</c> removes the downloads folder outright, with no prompt and no dry
/// run (<c>commands/service_cache_clean.go</c>). So the plan asks the CLI where that folder is (see
/// <see cref="ArduinoSettings"/>) and runs the command only where the folder holds nothing but the two
/// folders the CLI stages archives in, <c>packages</c> and <c>libraries</c>, and is not, and does not
/// hold, the data folder, the sketchbook or the build cache. Anything else there is §5.2's
/// unrecognised, and the command cannot be told to leave it, so the row offers nothing there and says
/// why.</para>
///
/// <para><b>The build cache, by path.</b> No command clears it. Each entry the CLI made carries its
/// <c>.last-used</c> marker, and only those are offered. See <see cref="ArduinoBuildCache"/>.</para>
///
/// <para><b>Tier 1.</b> An archive is read again only to reinstall the same version, which downloads
/// it again. A compiled core or sketch is rebuilt by the next compile.</para>
///
/// <para><b>§5.2 survivors.</b> The data folder and everything in it beside the downloads folder:
/// <c>packages</c>, which holds the installed cores and tools, <c>internal</c>, the indexes,
/// <c>inventory.yaml</c> and <c>arduino-cli.yaml</c>. The sketchbook. Never <c>Arduino15</c> as a
/// unit.</para>
///
/// <para>Before arduino-cli 1.0.4 the build cache was <c>%TEMP%\arduino</c>, which the temporary files
/// row already reaches by age.</para>
/// </summary>
public sealed class ArduinoCacheProvider : CleanupProviderBase
{
    private const string DownloadsGroup = "Downloaded archives";

    private const string BuildCacheGroup = "Build cache";

    private const string DataReason =
        "This is the Arduino data folder. It holds your installed boards and tools, the board and "
        + "library indexes and the CLI's settings. Deguffer removes only its downloaded archives.";

    private const string BuildCacheReason =
        "This is the Arduino build cache. Deguffer removes only the compiled cores and sketches the "
        + "Arduino CLI marked as its own.";

    private const string IdeConfigReason =
        "This is Arduino IDE 2's settings folder. Deguffer never removes anything here.";

    /// <summary>The folders the CLI stages archives in (the <c>CachePath</c> of each download resource).</summary>
    private static readonly DisposableChildSet StagingParts = new(
    [
        new ChildClassification("packages", SafetyTier.RegenerableCache, "Archives of boards and tools the Arduino CLI downloaded."),
        new ChildClassification("libraries", SafetyTier.RegenerableCache, "Archives of libraries the Arduino CLI downloaded."),
    ]);

    private static readonly DisposableChildSet DataParts = new(
    [
        new ChildClassification(ArduinoSettings.DefaultDownloadsName, SafetyTier.RegenerableCache, "Archives the Arduino CLI downloaded."),
    ]);

    private static readonly DisposableChildSet BuildCacheParts = new(
        ArduinoBuildCache.Parts.Select(part => new ChildClassification(part, SafetyTier.RegenerableCache, "Builds the Arduino CLI compiled.")));

    private readonly ISystemDirectories _system;
    private readonly PassMemo<Task<IReadOnlyList<Installation>>> _installations = new();

    public ArduinoCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
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

    public override string Id => "arduino";

    public override string Name => "Arduino downloads and build cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Installing a board or library version again downloads its archive again, and the next compile "
        + "of each sketch builds its core and sketch from scratch. Installed boards, tools and libraries, "
        + "your sketches and your settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Arduino IDE 2 and the Arduino CLI",
        Publisher = "Arduino",
        Purpose = "The Arduino tools keep every board, tool and library archive they download, and a "
            + "compiled copy of each board's core and each sketch, so the next install or compile is faster.",
        Recommendation = "Deguffer clears the downloaded archives with the Arduino CLI's own cache clean, "
            + "and removes only the build cache entries the CLI marked as its own. The CLI never trims "
            + "its current build cache itself.",
    };

    /// <summary>
    /// The CLI, and the IDE, which runs its bundled CLI as a daemon for as long as it is open. A clean
    /// while either downloads or compiles can remove an archive or a build it is writing.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["arduino-cli", "Arduino IDE"];

    /// <summary>The data folder where nothing moves it (<c>getDefaultArduinoDataDir</c>).</summary>
    public string DefaultData => Path.Combine(Environment.LocalAppData, "Arduino15");

    /// <summary>The build cache where nothing moves it: <c>os.UserCacheDir</c>, then <c>arduino</c>.</summary>
    public string DefaultBuildCache => Path.Combine(Environment.LocalAppData, "arduino");

    /// <summary>Arduino IDE 2's settings folder, which holds the configuration it hands its CLI.</summary>
    public string IdeConfigFolder => Path.Combine(Environment.UserProfile, ".arduinoIDE");

    /// <summary>The configuration file Arduino IDE 2 names to its CLI.</summary>
    public string IdeConfigFile => Path.Combine(IdeConfigFolder, "arduino-cli.yaml");

    /// <summary>
    /// The CLI Arduino IDE 2 installs with itself, in its per-user and its per-machine install folder
    /// (<c>resources.ts</c>: <c>resources</c> beside the IDE's bundled backend).
    /// </summary>
    private IEnumerable<string> BundledClis =>
    [
        Path.Combine(Environment.LocalAppData, "Programs", "Arduino IDE", "resources", "app", "lib", "backend", "resources", "arduino-cli.exe"),
        Path.Combine(_system.ProgramFiles, "Arduino IDE", "resources", "app", "lib", "backend", "resources", "arduino-cli.exe"),
    ];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. The data folder recognises only its staging folder,
    /// the build cache its two parts, and the IDE's settings folder nothing. Folders a setting moves
    /// are declared in <see cref="DiscoverToolRootsAsync"/>, because only the CLI can say.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Of(DefaultData, DataReason, DataParts),
        ToolRoot.Of(DefaultBuildCache, BuildCacheReason, BuildCacheParts),
        new ToolRoot(IdeConfigFolder, IdeConfigReason, static _ => false),
    ];

    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        List<ToolRoot> roots = [];

        foreach (var settings in (await InstallationsAsync(ct).ConfigureAwait(false)).Select(i => i.Settings).OfType<ArduinoSettings>())
        {
            var clearable = settings.Downloads.Folder is not null && WhyNotCleared(settings) is null;

            // The data folder recognises its staging folder only where that is where the downloads are
            // and the clean would remove them. Anywhere else, nothing in it is the row's.
            if (settings.Data.Folder is { } data)
            {
                roots.Add(clearable && IsStagingOf(settings.Downloads.Folder!, settings)
                    ? ToolRoot.Of(data, DataReason, DataParts)
                    : new ToolRoot(data, DataReason, static _ => false));
            }

            if (clearable && !IsStagingOf(settings.Downloads.Folder!, settings))
            {
                roots.Add(ToolRoot.Of(settings.Downloads.Folder!, "This is the folder the Arduino CLI stages downloaded archives in.", StagingParts));
            }

            if (settings.BuildCache.Folder is { } cache && WhyNotRead(settings) is null)
            {
                roots.Add(ToolRoot.Of(cache, BuildCacheReason, BuildCacheParts));
            }
        }

        return roots;
    }

    /// <summary>The default folders, and every folder a configuration moves them to that the plan would reach.</summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> places = [Path.Combine(DefaultData, ArduinoSettings.DefaultDownloadsName), DefaultBuildCache];

        foreach (var settings in (await InstallationsAsync(ct).ConfigureAwait(false)).Select(i => i.Settings).OfType<ArduinoSettings>())
        {
            if (settings.Downloads.Folder is { } downloads && WhyNotCleared(settings) is null)
            {
                places.Add(downloads);
            }

            if (settings.BuildCache.Folder is { } cache && WhyNotRead(settings) is null)
            {
                places.Add(cache);
            }
        }

        return [.. places.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnCli() is not null || IdeCli() is not null);

    /// <summary>Every answer is configuration, which the user can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _installations.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var installations = await InstallationsAsync(ct).ConfigureAwait(false);

        if (installations.Count == 0)
        {
            return EmptyPlan("Neither Arduino IDE 2 nor the Arduino CLI is installed on this machine.");
        }

        var known = installations.Where(i => i.Settings is not null).ToList();

        if (known.Count == 0)
        {
            return UnexaminedPlan(
                "The Arduino CLI did not say where its folders are, and its cache clean removes its downloads "
                + "folder outright, so Deguffer cannot tell what it would remove.");
        }

        var notes = new List<PlanNote>();
        var survivors = new List<(string Path, string Reason)>();
        var unreachable = new List<string>();

        if (installations.Count > known.Count)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "One Arduino configuration did not say where its folders are, so Deguffer leaves everything it "
                + "names alone."));
        }

        var (commands, staged) = await PlanDownloadsAsync(known, notes, survivors, unreachable, ct).ConfigureAwait(false);
        var (builds, built) = await PlanBuildCachesAsync(known, keep, notes, survivors, unreachable, ct).ConfigureAwait(false);

        // After the clean is planned, because one configuration's data folder can hold the downloads
        // folder another configuration's clean removes, and that folder is not a survivor of either.
        IReadOnlyList<string> removed = [.. commands.OfType<RunCommandStep>().SelectMany(step => step.MeasuredPaths)];

        foreach (var settings in known.Select(i => i.Settings!))
        {
            survivors.AddRange(Survivors(settings, removed));
        }

        foreach (var note in new[] { staged.Note, built.Note, BuildRunningProcessNote() })
        {
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        if (commands.Count == 0 && builds.Count == 0 && notes.Count == 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, "Arduino has downloaded and built nothing that is still kept."));
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = [.. commands, .. builds],
            ProtectedPaths = Protect([.. survivors]),
            Notes = notes,
            Fallback = staged.Fallback is not FallbackReason.None ? staged.Fallback : built.Fallback,
            HasUnreadableRoot = unreachable.Count > 0,
        };
    }

    /// <summary>
    /// One <c>cache clean</c> per downloads folder that may be removed outright and holds something.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanDownloadsAsync(
        IReadOnlyList<Installation> known,
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        List<string> unreachable,
        CancellationToken ct)
    {
        var cleared = new List<Installation>();

        foreach (var installation in known.DistinctBy(i => i.Settings!.Downloads.Folder ?? i.Settings.Downloads.Reported, StringComparer.OrdinalIgnoreCase))
        {
            var settings = installation.Settings!;

            if (WhyNotCleared(settings) is { } declined)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, declined));

                if (settings.Downloads.Folder is { } kept)
                {
                    survivors.Add((kept, "The Arduino downloads folder, which Deguffer is leaving alone."));
                }

                continue;
            }

            var downloads = settings.Downloads.Folder!;

            switch (WhyNotEmptied(downloads))
            {
                case { Absent: true }:
                    continue;
                case { Unlisted: true }:
                    unreachable.Add(downloads);
                    notes.Add(UnreadableRoot.Note(downloads));
                    survivors.Add((downloads, "Windows would not list the Arduino downloads folder, so Deguffer leaves it alone."));
                    continue;
                case { Why: { } why }:
                    notes.Add(new PlanNote(PlanNoteSeverity.Information, why));
                    survivors.Add((downloads, "The Arduino downloads folder holds something the Arduino CLI did not put there."));
                    continue;
            }

            cleared.Add(installation);
        }

        var measured = await MeasureAllAsync([.. cleared.Select(i => i.Settings!.Downloads.Folder!)], ct).ConfigureAwait(false);

        IReadOnlyList<CleanupStep> steps =
        [
            .. cleared.Select((installation, index) => new RunCommandStep(
                installation.Cli,
                ArduinoSettings.CleanArguments(installation.Settings!.ConfigFile),
                "Delete the downloaded Arduino archives using the Arduino CLI's own cache clean")
            {
                // A clean cut off part-way leaves fewer archives, and the CLI checks an archive's size
                // and checksum before it installs from one, so a half-removed one is downloaded again.
                // It is left to finish all the same: it takes seconds, and its removal is one RemoveAll.
                WhenCancelled = CancelledCommand.Finishes,
                Estimated = measured.Sizes[index],
                MeasuredPaths = [installation.Settings.Downloads.Folder!],
                Group = DownloadsGroup,
            }),
        ];

        return (steps, measured);
    }

    /// <summary>One removal per build cache entry the CLI marked as its own.</summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanBuildCachesAsync(
        IReadOnlyList<Installation> known,
        MinimumAge keep,
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        List<string> unreachable,
        CancellationToken ct)
    {
        var targets = new List<DeletionTarget>();

        foreach (var settings in known.Select(i => i.Settings!).DistinctBy(s => s.BuildCache.Folder ?? s.BuildCache.Reported, StringComparer.OrdinalIgnoreCase))
        {
            if (WhyNotRead(settings) is { } declined)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, declined));
                continue;
            }

            var cache = settings.BuildCache.Folder!;

            if (LongPath.ProbeDirectory(cache) is PathPresence.Absent)
            {
                continue;
            }

            if (LongPath.IsReparsePoint(cache))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving the Arduino build cache '{cache}' alone: it is a link to somewhere else, and Deguffer "
                    + "does not delete through a link."));
                survivors.Add((cache, BuildCacheReason));
                continue;
            }

            var scan = ArduinoBuildCache.Read(cache);

            survivors.Add((cache, BuildCacheReason));
            survivors.AddRange(scan.Unrecognised);

            foreach (var folder in scan.Unlisted)
            {
                unreachable.Add(folder);
                notes.Add(UnreadableRoot.Note(folder));
                survivors.Add((folder, "Windows would not list this part of the Arduino build cache, so Deguffer leaves it alone."));
            }

            targets.AddRange(scan.Entries.Select(entry => new DeletionTarget(
                entry.Path,
                entry.Kind == "cores"
                    ? "A board core the Arduino CLI compiled. The next compile for that board builds it again."
                    : "A sketch build the Arduino CLI made. The next compile of that sketch builds it again.",
                LastWritten: entry.LastUsed,
                Group: BuildCacheGroup)));
        }

        return await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why <c>cache clean</c> must not be run with these settings, as a sentence, or null where it may.
    /// Asked of what the settings name, before anything on the disk.
    /// </summary>
    private string? WhyNotCleared(ArduinoSettings settings)
    {
        if (settings.Downloads.Folder is not { } downloads)
        {
            return $"The Arduino CLI is set to keep its downloads in '{settings.Downloads.Reported}', which is not a "
                + "full path, so Deguffer cannot tell which folder its cache clean would remove.";
        }

        if (ConfiguredFolder.WhyNotOwned(
                downloads, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned)
        {
            return $"The Arduino CLI is set to keep its downloads in '{downloads}', and Deguffer will not run its cache "
                + $"clean there: {notOwned} It is leaving it alone.";
        }

        // cache clean removes the folder itself, so it must be none of the folders that matter and hold
        // none of them.
        var folder = Reach(downloads);

        foreach (var (name, other) in Others(settings, settings.Downloads))
        {
            var reached = Reach(other);

            if (folder.IsSameAs(reached) || folder.Holds(reached) || (reached.Holds(folder) && !IsStagingOf(downloads, settings)))
            {
                return $"The Arduino CLI is set to keep its downloads in '{downloads}', which is or holds its {name}, "
                    + "and its cache clean removes that folder outright. Deguffer is leaving it alone.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why the build cache these settings name must not be read as the CLI's, as a sentence, or null
    /// where it may.
    /// </summary>
    private string? WhyNotRead(ArduinoSettings settings)
    {
        if (settings.BuildCache.Folder is not { } cache)
        {
            return $"The Arduino CLI is set to keep its build cache in '{settings.BuildCache.Reported}', which is not a "
                + "full path, so Deguffer cannot tell which folder it means.";
        }

        if (ConfiguredFolder.WhyNotOwned(
                cache, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned)
        {
            return $"The Arduino CLI is set to keep its build cache in '{cache}', and Deguffer will not treat it as "
                + $"the CLI's: {notOwned} It is leaving it alone.";
        }

        var folder = Reach(cache);

        foreach (var (name, other) in Others(settings, settings.BuildCache))
        {
            var reached = Reach(other);

            if (folder.IsSameAs(reached) || folder.Holds(reached) || reached.Holds(folder))
            {
                return $"The Arduino CLI is set to keep its build cache in '{cache}', which overlaps its {name}. "
                    + "Deguffer cannot tell the CLI's builds from the rest, so it is leaving it alone.";
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
    private bool IsStagingOf(string downloads, ArduinoSettings settings) =>
        settings.Data.Folder is { } data
        && Reach(downloads).IsSameAs(Reach(Path.Combine(data, ArduinoSettings.DefaultDownloadsName)));

    /// <summary>
    /// Why the downloads folder as it stands on the disk must not be removed, or nothing where it may.
    /// <c>cache clean</c> removes every entry, and follows nothing, but a link at the folder itself is
    /// declined: what it points at was never classified.
    /// </summary>
    private static (bool Absent, bool Unlisted, string? Why) WhyNotEmptied(string downloads)
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
    private IEnumerable<(string Path, string Reason)> Survivors(ArduinoSettings settings, IReadOnlyList<string> removed)
    {
        if (settings.Data.Folder is { } data)
        {
            yield return (data, "The Arduino data folder, with your installed boards, tools and indexes.");

            foreach (var entry in FolderEntries.Of(data) ?? [])
            {
                var path = LongPath.Display(entry.FullName);

                if (!removed.Any(folder => Reach(folder).IsSameAs(Reach(path))))
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

    /// <summary>
    /// Each configuration on this machine, with the CLI that answers for it and what it said, asked once
    /// per planning pass because the presence probe, Explore and the plan all need it.
    /// </summary>
    private Task<IReadOnlyList<Installation>> InstallationsAsync(CancellationToken ct) =>
        _installations.Get(async () =>
        {
            List<(string Cli, string? ConfigFile)> asked = [];

            if ((OwnCli() ?? IdeCli()) is { } own)
            {
                asked.Add((own, null));
            }

            if (LongPath.ProbeFile(IdeConfigFile) is PathPresence.Present && (IdeCli() ?? OwnCli()) is { } ide)
            {
                asked.Add((ide, IdeConfigFile));
            }

            var installations = new List<Installation>(asked.Count);

            foreach (var (cli, configFile) in asked)
            {
                installations.Add(new Installation(
                    cli,
                    await ArduinoSettings.AskAsync(Runner, cli, configFile, ct).ConfigureAwait(false)));
            }

            return (IReadOnlyList<Installation>)installations;
        });

    /// <summary>The Arduino CLI on the PATH, which answers for its own configuration.</summary>
    private string? OwnCli() => Environment.FindExecutable("arduino-cli");

    /// <summary>The CLI Arduino IDE 2 installed with itself, which answers for the IDE's configuration.</summary>
    private string? IdeCli() => BundledClis.FirstOrDefault(path => LongPath.ProbeFile(path) is PathPresence.Present);

    /// <param name="Cli">The executable that answered, and that the clean runs.</param>
    /// <param name="Settings">What it said, or null where any answer was missing.</param>
    private sealed record Installation(string Cli, ArduinoSettings? Settings);
}
