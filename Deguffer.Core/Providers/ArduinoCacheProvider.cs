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
/// <para><b>Two environments.</b> Where the machine's environment changed after Deguffer started, each
/// configuration is answered twice (see <see cref="ArduinoInstallations"/>). The plan runs and measures
/// from the answer of the CLI its clean starts. Explore, a duplicate search and §5.6 hear both, and a
/// clean is declined where it would reach a folder a CLI the user starts now uses: a declaration only
/// narrows what may be removed.</para>
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

    private static readonly DisposableChildSet DataParts = new(
    [
        new ChildClassification(ArduinoSettings.DefaultDownloadsName, SafetyTier.RegenerableCache, "Archives the Arduino CLI downloaded."),
    ]);

    private static readonly DisposableChildSet BuildCacheParts = new(
        ArduinoBuildCache.Parts.Keys.Select(part => new ChildClassification(part, SafetyTier.RegenerableCache, "Builds the Arduino CLI compiled.")));

    private readonly ISystemDirectories _system;
    private readonly ArduinoFolderRules _rules;
    private readonly ArduinoInstallations _installations;

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
        _rules = new ArduinoFolderRules(Environment, _system, Volumes, Reach);
        _installations = new ArduinoInstallations(Environment, Runner, [.. BundledClis], IdeConfigFile, DefaultData);
    }

    public override string Id => "arduino";

    public override string Name => "Arduino downloads and build cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Installing a board or library version again downloads its archive again, and the next compile "
        + "of each sketch builds its core and sketch from scratch. Installed boards, tools and libraries, "
        + "your sketches and your settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

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
    /// are declared in <see cref="DiscoverToolRootsAsync"/>, because only the CLI can say, and it says
    /// for the CLI Deguffer starts and for one the user starts now.
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

        foreach (var settings in (await _installations.AskAsync(ct).ConfigureAwait(false)).Answered)
        {
            var clearable = settings.Downloads.Folder is not null && _rules.WhyNotCleared(settings) is null;

            // The data folder recognises its staging folder only where that is where the downloads are
            // and the clean would remove them. Anywhere else, nothing in it is the row's.
            if (settings.Data.Folder is { } data)
            {
                roots.Add(clearable && _rules.IsStagingOf(settings.Downloads.Folder!, settings)
                    ? ToolRoot.Of(data, DataReason, DataParts)
                    : new ToolRoot(data, DataReason, static _ => false));
            }

            if (clearable && !_rules.IsStagingOf(settings.Downloads.Folder!, settings))
            {
                roots.Add(ToolRoot.Of(settings.Downloads.Folder!, "This is the folder the Arduino CLI stages downloaded archives in.", ArduinoFolderRules.StagingParts));
            }

            if (settings.BuildCache.Folder is { } cache && _rules.WhyNotRead(settings) is null)
            {
                roots.Add(ToolRoot.Of(cache, BuildCacheReason, BuildCacheParts));
            }
        }

        return roots;
    }

    /// <summary>
    /// The default folders, and every folder a configuration moves them to that the plan would reach,
    /// as the CLI Deguffer starts reads it or as one the user starts now does.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> places = [Path.Combine(DefaultData, ArduinoSettings.DefaultDownloadsName), DefaultBuildCache];

        foreach (var settings in (await _installations.AskAsync(ct).ConfigureAwait(false)).Answered)
        {
            if (settings.Downloads.Folder is { } downloads && _rules.WhyNotCleared(settings) is null)
            {
                places.Add(downloads);
            }

            if (settings.BuildCache.Folder is { } cache && _rules.WhyNotRead(settings) is null)
            {
                places.Add(cache);
            }
        }

        return [.. places.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(_installations.AnyCli);

    /// <summary>Every answer is configuration, which the user can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _installations.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Every Arduino configuration's folders, asked as the pass begins: four <c>config get</c> starts in
    /// turn for each CLI and configuration, and again for each where the machine's variables have changed,
    /// which is up to sixteen and all of this row's wait before it can look at the disk. Where no CLI is
    /// found, the answers ask nothing.
    /// </summary>
    public override Task BeginPass(PassStart start) => start.Ask(_installations.AskAsync);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var answers = await _installations.AskAsync(ct).ConfigureAwait(false);
        var installations = answers.Tool;

        if (installations.Count == 0)
        {
            return EmptyPlan("Neither Arduino IDE 2 nor the Arduino CLI is installed on this machine.");
        }

        var known = installations.Where(i => i.Settings is not null).ToList();

        if (known.Count == 0)
        {
            return UnexaminedPlan(
                "The Arduino CLI did not say where its folders are, or its configuration changed while it was asked, and its "
                + "cache clean removes its downloads "
                + "folder outright, so Deguffer cannot tell what it would remove.");
        }

        var notes = new List<PlanNote>();
        var survivors = new List<(string Path, string Reason)>();
        var unreachable = new List<string>();

        if (installations.Count > known.Count)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "One Arduino configuration did not say where its folders are, or changed while it was asked, so "
                + "Deguffer leaves everything it names alone."));
        }

        if (answers.StartedNowUnknown)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "A setting that moves Arduino's folders changed on this machine after Deguffer started, and an Arduino "
                + "CLI started with it did not say where its folders are, or reads a configuration file Deguffer cannot "
                + "check, so Deguffer cannot tell what a clean would reach. It leaves the downloads and the build cache "
                + "alone while that is so."));
        }

        var (commands, staged) = await PlanDownloadsAsync(known, answers, notes, survivors, unreachable, ct).ConfigureAwait(false);
        var (builds, built) = await PlanBuildCachesAsync(known, answers, keep, notes, survivors, unreachable, ct).ConfigureAwait(false);

        // After the clean is planned, because one configuration's data folder can hold the downloads
        // folder another configuration's clean removes, and that folder is not a survivor of either.
        IReadOnlyList<string> removed = [.. commands.OfType<RunCommandStep>().SelectMany(step => step.MeasuredPaths)];

        // A CLI the user starts now keeps its folders where its answer says, so they survive as the clean's do.
        foreach (var settings in known.Select(i => i.Settings!).Concat(answers.StartedNow).Distinct())
        {
            survivors.AddRange(_rules.Survivors(settings, removed));

            if (settings.ConfigFile is { } ideConfig)
            {
                survivors.Add((ideConfig, "The settings Arduino IDE 2 hands its Arduino CLI."));
            }
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
            ProtectedPaths = await ProtectAsync([.. survivors], ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = FallbackReasonText.Prevailing(staged.Fallback, built.Fallback),
            HasUnreadableRoot = unreachable.Count > 0,
        };
    }

    /// <summary>
    /// One <c>cache clean</c> per downloads folder that may be removed outright and holds something.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanDownloadsAsync(
        IReadOnlyList<ArduinoInstallation> known,
        ArduinoAnswers answers,
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        List<string> unreachable,
        CancellationToken ct)
    {
        var cleared = new List<ArduinoInstallation>();

        foreach (var installation in known.DistinctBy(i => i.Settings!.Downloads.Folder ?? i.Settings.Downloads.Reported, StringComparer.OrdinalIgnoreCase))
        {
            var settings = installation.Settings!;

            if (_rules.WhyNotCleared(settings) is { } declined)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, declined));

                if (settings.Downloads.Folder is { } kept)
                {
                    survivors.Add((kept, "The Arduino downloads folder, which Deguffer is leaving alone."));
                }

                continue;
            }

            var downloads = settings.Downloads.Folder!;

            if (answers.ReachesStartedNow(downloads, _rules.WhyNotClearedBeside, notes))
            {
                survivors.Add((downloads, "The Arduino downloads folder, which Deguffer is leaving alone."));
                continue;
            }

            if (installation.Configuration.WhyUnknown is { } unknown)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    "Deguffer cannot check before the clean that the Arduino CLI still means the folder it named, "
                    + $"because {unknown} Its cache clean removes the folder it is sent to, so the downloads are left alone."));
                survivors.Add((downloads, "The Arduino downloads folder, which Deguffer is leaving alone."));
                continue;
            }

            switch (ArduinoFolderRules.WhyNotEmptied(downloads))
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
                TargetCheck = new ArduinoCleanTarget(
                    () => _installations.ReadConfiguration(VariableReading.Tool, installation.Settings.ConfigFile),
                    installation.Configuration,
                    new ArduinoAnswersCheck(
                        Environment.ReadEnvironmentNow,
                        answers,
                        configFile => _installations.ReadConfiguration(VariableReading.Tool, configFile),
                        _installations.ReadConfiguration),
                    installation.Settings.Downloads.Folder!),
            }),
        ];

        return (steps, measured);
    }

    /// <summary>One removal per build cache entry the CLI marked as its own.</summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanBuildCachesAsync(
        IReadOnlyList<ArduinoInstallation> known,
        ArduinoAnswers answers,
        MinimumAge keep,
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        List<string> unreachable,
        CancellationToken ct)
    {
        var targets = new List<DeletionTarget>();

        foreach (var settings in known.Select(i => i.Settings!).DistinctBy(s => s.BuildCache.Folder ?? s.BuildCache.Reported, StringComparer.OrdinalIgnoreCase))
        {
            if (_rules.WhyNotRead(settings) is { } declined)
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, declined));
                continue;
            }

            var cache = settings.BuildCache.Folder!;

            if (answers.ReachesStartedNow(cache, _rules.WhyNotReadBeside, notes))
            {
                survivors.Add((cache, BuildCacheReason));
                continue;
            }

            if (LongPath.ProbeDirectory(cache, out var cacheIsLink) is PathPresence.Absent)
            {
                continue;
            }

            // Not false rather than true: a folder Windows would not describe reaches this line, and
            // is declined with the link because nothing in it was classified.
            if (cacheIsLink is not false)
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
                entry.Part is ArduinoBuildPart.Cores
                    ? "A board core the Arduino CLI compiled. The next compile for that board builds it again."
                    : "A sketch build the Arduino CLI made. The next compile of that sketch builds it again.",
                LastWritten: entry.LastUsed,
                Group: BuildCacheGroup)));
        }

        return await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);
    }
}
