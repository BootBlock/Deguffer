using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The local cache restic, the backup program, keeps of each repository's metadata. Researched rather
/// than measured: no restic was installed on the machine this was written against, so every rule here
/// is from restic's source (<c>internal/backend/cache</c>, <c>cmd/restic/cmd_cache.go</c>) and its
/// documentation.
///
/// <para><b>Tier 1.</b> restic: "The cache is ephemeral: When a file cannot be read from the cache, it
/// is loaded from the repository." Every byte is a copy of something in the repository, and the cost
/// of removing it is a slower next run.</para>
///
/// <para><b>The folder is <c>%LOCALAPPDATA%\restic</c>, or wherever <c>RESTIC_CACHE_DIR</c> names.</b>
/// restic takes the variable, then Go's user cache folder, which on Windows is the local application
/// data folder (<c>cache.DefaultDir</c>). It has no configuration file that moves it. A
/// <c>--cache-dir</c> given on a command line is invisible to anything but that command.</para>
///
/// <para><b>§5.1: restic's own <c>cache --cleanup</c>, sent to each cache folder by name.</b> With
/// <c>--max-age 0</c> it removes every repository's cache, and <c>--cache-dir</c> sends it to the
/// folder the plan examined rather than the one its own environment names. It removes each folder
/// it takes whole, so it is sent only where everything it would take is a repository's cache holding
/// what restic writes there (<see cref="ResticCacheLayout.WhyNotWhole"/>). Elsewhere, and where restic
/// cannot be found, the three folders restic caches into are removed by path.</para>
///
/// <para><b>§5.2.</b> The cache folder is never a target. Only a repository's cache is recognised in
/// it, and only <c>data</c>, <c>index</c> and <c>snapshots</c> inside one. The repository itself,
/// which may be a folder on the same disk, is never in the cache folder, and nothing here reads or
/// names it.</para>
///
/// <para><b>Nothing while restic runs.</b> A backup, check or prune that is running reads and writes
/// the cache, so while restic is in the process table the caches are held back, and the clean asks
/// again before it removes anything.</para>
/// </summary>
public sealed class ResticCacheProvider : CleanupProviderBase
{
    private static readonly IReadOnlyList<string> ProcessNames = ["restic"];

    private const string HeldReason =
        "restic is running, and a backup, check or prune that is running uses its cache, so the cache is left "
        + "alone. Scan again once restic has finished.";

    private const string RootReason =
        "This is restic's cache folder. Deguffer removes only what restic cached for each repository inside it, "
        + "never the folder itself.";

    private const string CacheReason =
        "This is restic's cache for one repository. Only its data, index and snapshots folders may be removed.";

    private readonly ISystemDirectories _system;
    private readonly RowDeclarations _declarations;
    private readonly PassMemo<IReadOnlyList<ResticCacheFolder>> _folders = new();

    /// <param name="declarations">
    /// What every row in the planner offers, so something another row removes from a folder this row
    /// reads is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public ResticCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        RowDeclarations? declarations = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _declarations = declarations ?? new RowDeclarations();
    }

    public override string Id => "restic-cache";

    public override string Name => "restic local cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override string WhatHappensOnNextUse =>
        "Your next restic run re-downloads this metadata from the repository. Nothing in your backups changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "restic, a backup program",
        Publisher = "the restic project",
        Purpose = "restic keeps a local copy of each repository's index, snapshot records and folder listings, so "
            + "that a backup or a restore does not read them again from the repository, which may be on a slow "
            + "disk or in the cloud.",
        Recommendation = "restic's documentation calls this cache ephemeral: anything missing from it is read from "
            + "the repository again. Deguffer clears it with restic's own cache command where it can, and removes "
            + "nothing while restic is running.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ProcessNames;

    /// <summary>Where restic keeps its cache when <see cref="ResticCacheLayout.CacheVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "restic");

    /// <summary>§5.2 for Explore: each cache folder recognises only the repository caches in it.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        [.. Roots().Select(root => ToolRoot.Folders(root, RootReason, ResticCacheLayout.IsRepositoryCache))];

    /// <summary>
    /// Each repository's cache recognises only the folders restic caches into, and nothing at all while
    /// restic runs.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var running = ResticIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. Folders().SelectMany(folder => folder.Caches).Select(cache => running
                ? new ToolRoot(cache, HeldReason, static _ => false)
                : ToolRoot.Of(cache, CacheReason, ResticCacheLayout.Children)),
        ]);
    }

    /// <summary>
    /// Each repository's cache whole, because restic's cleanup removes it whole. A removal by path takes
    /// less, which only ever makes a duplicate there less likely to be kept.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Folders().SelectMany(folder => folder.Caches).Select(CleanedPlace.Whole)]);

    /// <summary>
    /// A repository's cache on the disk, or a cache folder that could not be examined, never restic on the
    /// PATH: a cache outlives the restic that wrote it.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Folders().Any(folder => folder.Caches.Count > 0 || folder.Declined.Count > 0 || folder.Unreadable));

    /// <summary>The variable and the disk can both change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _folders.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var notes = new List<PlanNote>(DeclinedSettingNotes());
        var folders = Folders();

        if (!folders.Any(folder => folder.Caches.Count > 0 || folder.Declined.Count > 0 || folder.Unreadable))
        {
            return notes.Count > 0
                ? UnexaminedPlan(string.Join(" ", notes.Select(note => note.Message)))
                : EmptyPlan("restic has cached no repository for this account.");
        }

        var running = ResticIsRunning();
        var restic = Environment.FindExecutable("restic");
        var steps = new List<CleanupStep>();
        var survivors = new List<(string Path, string Reason)>();
        var held = new List<(string Path, string Reason)>();
        var fallback = FallbackReason.None;
        var unreadable = false;

        foreach (var folder in folders)
        {
            notes.AddRange(folder.Notes);
            unreadable |= folder.Unreadable;
            survivors.Add((folder.Root, "restic's cache folder must survive. Only what it cached for each repository is removed."));
            survivors.AddRange(folder.Declined);
            survivors.AddRange(await SparedAsync(folder.Unrecognised, ct).ConfigureAwait(false));

            if (folder.Caches.Count == 0)
            {
                continue;
            }

            if (running)
            {
                held.AddRange(folder.Caches.Select(cache => (cache, HeldReason)));
                continue;
            }

            var whyNotWhole = restic is null ? null : ResticCacheLayout.WhyNotWhole(folder.Root);

            if (restic is not null && whyNotWhole is null)
            {
                var measured = await MeasureAllAsync(folder.Caches, ct).ConfigureAwait(false);

                steps.Add(new RunCommandStep(
                    restic,
                    $"--cache-dir \"{LongPath.Display(folder.Root)}\" cache --cleanup --max-age 0",
                    "Clear restic's cache using restic's own command")
                {
                    // restic removes one whole file at a time, and reads any file missing from the cache from
                    // the repository, so a cleanup ended part-way leaves a smaller cache and nothing worse.
                    WhenCancelled = CancelledCommand.Ended,
                    Estimated = measured.Total,
                    MeasuredPaths = folder.Caches,
                    TargetCheck = new ResticCleanupTarget(folder.Root, Inspector, ProcessNames),
                });

                fallback = FallbackReasonText.Prevailing(fallback, measured.Fallback);
                continue;
            }

            var (removals, removed, walked) = await PlanRemovalsByPathAsync(folder, keep, ct).ConfigureAwait(false);

            steps.AddRange(removals);
            survivors.AddRange(walked.Survivors);
            unreadable |= walked.Unreadable;
            notes.AddRange(walked.Notes);
            fallback = FallbackReasonText.Prevailing(fallback, removed.Fallback);

            if (removals.Count > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    restic is null
                        ? "Deguffer cannot find restic, so it removes what restic cached by path rather than with restic's own command."
                        : $"Deguffer removes what restic cached by path rather than with restic's own cleanup: {whyNotWhole}, "
                            + "and the cleanup would remove it with the rest."));
            }
        }

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left restic's cache alone: {HeldReason}"));
        }

        // Said once for every measurement, which meet the same volumes and give the same reason.
        if (FallbackReasonText.Describe(fallback) is { } route)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, route));
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a cache held back is named for why rather than as the folder only some of the
            // contents of are removed.
            ProtectedPaths = await ProtectAsync([.. held, .. survivors], ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = fallback,
            WasNotExamined = steps.Count == 0
                && (held.Count > 0 || folders.Any(folder => folder.Declined.Count > 0) || unreadable),
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// The folders restic caches into, in each repository's cache in <paramref name="folder"/>, with each
    /// repository's cache, the version file in it and anything unrecognised named as survivors.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured, WalkedCaches Walked)> PlanRemovalsByPathAsync(
        ResticCacheFolder folder,
        MinimumAge keep,
        CancellationToken ct)
    {
        var stillClosed = new RunningProcessCheck(Inspector, ProcessNames);
        var targets = new List<DeletionTarget>();
        var survivors = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var unreadable = false;

        foreach (var cache in folder.Caches)
        {
            var walk = CacheLevelWalk.Under(ResticCacheLayout.Levels, cache, ct);

            // restic shows a repository by the first ten digits of its identifier, so the heading does too.
            var heading = $"Repository {Path.GetFileName(cache)[..10]}";

            targets.AddRange(walk.Targets.Select(target => target with { Group = heading, UseCheck = stillClosed }));
            survivors.Add((cache, "restic's cache for this repository must survive. Only the folders it caches into are removed."));
            survivors.Add((Path.Combine(cache, ResticCacheLayout.VersionFile), "The file restic records this cache's layout in."));
            survivors.AddRange(walk.Declined);
            survivors.AddRange(await walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false));
            notes.AddRange(walk.Notes);
            unreadable |= walk.Unreadable;
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        return (steps, measured, new WalkedCaches(survivors, notes, unreadable));
    }

    /// <summary>Everything unrecognised in a cache folder that no other row offers.</summary>
    private async Task<IReadOnlyList<(string Path, string Reason)>> SparedAsync(
        IReadOnlyList<(string Path, string Reason)> unrecognised,
        CancellationToken ct)
    {
        var spared = new List<(string Path, string Reason)>();

        foreach (var entry in unrecognised)
        {
            if (!await _declarations.OfferedByARowAsync(entry.Path, ct).ConfigureAwait(false))
            {
                spared.Add(entry);
            }
        }

        return spared;
    }

    private IEnumerable<PlanNote> DeclinedSettingNotes() =>
        Readings()
            .Where(setting => setting.Declined is not null)
            .DistinctBy(setting => setting.Value, StringComparer.OrdinalIgnoreCase)
            .Select(setting => new PlanNote(
                PlanNoteSeverity.Information,
                $"{ResticCacheLayout.CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as "
                + $"restic's cache folder: {setting.Declined} It is leaving it alone."));

    private bool ResticIsRunning() => Inspector.FindRunning(ProcessNames).Count > 0;

    private IReadOnlyList<ResticCacheFolder> Folders() =>
        _folders.Get(() => [.. Roots().Select(ResticCacheFolder.Read).OfType<ResticCacheFolder>()]);

    /// <summary>
    /// Every cache folder in use: the one a restic the user starts reads, the one a restic Deguffer starts
    /// reads, and the default, which still holds what restic cached before the variable moved it.
    /// </summary>
    private IEnumerable<string> Roots() =>
        Readings()
            .Select(setting => setting.Folder)
            .Append(DefaultRoot)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ConfiguredFolder.Setting> Readings() =>
    [
        ConfiguredFolder.FromVariable(VariableReading.Current, ResticCacheLayout.CacheVariable, DefaultRoot, Environment, _system, Volumes),
        ConfiguredFolder.FromVariable(VariableReading.Tool, ResticCacheLayout.CacheVariable, DefaultRoot, Environment, _system, Volumes),
    ];

    /// <summary>What walking the repository caches in one cache folder found besides what it offers.</summary>
    private sealed record WalkedCaches(
        IReadOnlyList<(string Path, string Reason)> Survivors,
        IReadOnlyList<PlanNote> Notes,
        bool Unreadable);
}
