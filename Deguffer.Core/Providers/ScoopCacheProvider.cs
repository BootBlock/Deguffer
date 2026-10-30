using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Scoop's download cache: the installer archives Scoop keeps so that installing the same version
/// again needs no download. Researched rather than measured: no Scoop was installed on the machine
/// this was written against. Sources are ScoopInstaller/Scoop at
/// <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c> and ScoopInstaller/Install at
/// <c>1e2f334083d609986d8c8bc9e31ae8e87c39fab4</c>.
///
/// <para><b>§5.1, through <c>scoop cache rm *</c>.</b> <c>*</c>, <c>-a</c> and <c>--all</c> share one
/// branch of <c>cacheremove</c>, which removes every top-level entry of the cache folder with
/// <c>Remove-Item</c> and leaves the folder itself. Nothing on the <c>cache</c> path checks for
/// updates, touches the network or runs git, and nothing reads standard input except the prompt
/// <c>Remove-Item</c> raises for a non-empty folder, which the plan never lets the command meet.
/// <c>scoop cleanup</c> is a different command and is never run: it removes old versions of
/// installed apps, which is uninstalling.</para>
///
/// <para><b>§5.2 decides whether the command is offered at all.</b> The command removes what it
/// lists without reading the names, so it is offered only where every entry in the folder is one
/// Scoop wrote (<see cref="ScoopCacheEntries"/>). Anything else there means no step, a note naming
/// it, and its survival asserted.</para>
///
/// <para><b>Global installs share this cache.</b> <c>cache_path</c> takes no <c>$global</c>, so
/// <c>scoop install --global</c> downloads into the same per-user folder.</para>
/// </summary>
public sealed class ScoopCacheProvider : CleanupProviderBase
{
    private const string Arguments = "cache rm *";

    /// <summary>How many unrecognised entries the note names before it summarises the rest.</summary>
    private const int EntriesNamed = 3;

    private readonly ISystemDirectories _system;
    private readonly PassMemo<ScoopResolution> _resolution = new();

    public ScoopCacheProvider(
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

    public override string Id => "scoop-cache";

    public override string Name => "Scoop download cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next Scoop install or update that needs a removed download fetches it again. Installed "
        + "apps, their persisted data and your buckets are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Scoop, a command-line installer for Windows",
        Publisher = "the open-source Scoop project",
        Purpose = "Scoop keeps a copy of every installer archive it downloads, so that installing the "
            + "same version of an app again is served from disk.",
        Recommendation = "Deguffer runs Scoop's own cache command, and only where everything in the "
            + "cache folder is a download Scoop made. Installed apps are untouched.",
    };

    /// <summary>Scoop's aria2 helper downloads straight into the cache.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["aria2c"];

    /// <summary>Scoop's root where nothing has moved it.</summary>
    private string DefaultRoot => Path.Combine(Environment.UserProfile, "scoop");

    /// <summary>§5.2 as §7.1 needs it read from outside, at the default locations.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ScoopToolRoots.Of(DefaultRoot, Path.Combine(DefaultRoot, "cache"), [Path.Combine(Environment.UserProfile, ".config", "scoop", "config.json")]);

    /// <summary>The same declaration at the folders the running Scoop uses.</summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult(ScoopToolRoots.Of(Resolve()));

    /// <summary>
    /// The cache the command is sent to, and the default one, because <c>SCOOP_CACHE</c> or a
    /// setting can move it back after a duplicate search has asked.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> places = [Path.Combine(DefaultRoot, "cache")];

        if (Resolve() is ScoopResolution.Followed followed)
        {
            places.Add(followed.Layout.Cache);
        }

        return Task.FromResult<IReadOnlyList<CleanedPlace>>(
            [.. places.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)]);
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("scoop") is not null);

    /// <summary>
    /// The settings file and both environment variables move the cache, so a remembered answer
    /// would examine a folder Scoop has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolution.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct) => Resolve() switch
    {
        ScoopResolution.Followed followed => await PlanCacheAsync(followed.Layout, ct).ConfigureAwait(false),
        ScoopResolution.Unfollowed unfollowed => UnexaminedPlan(unfollowed.Why),
        ScoopResolution.RootDeclined declined => UnexaminedPlan(
            $"Scoop is set to keep its apps and its cache in {declined.Layout.Root}, which Deguffer will not "
            + $"examine as Scoop's own folder: {declined.Why} It is leaving Scoop's cache alone."),
        ScoopResolution.CacheDeclined declined => UnexaminedPlan(
            $"Scoop is set to keep its downloads in {declined.Layout.Cache}, which Deguffer will not examine "
            + $"as Scoop's own folder: {declined.Why} Scoop's own command removes everything there, so it is not run."),
        _ => EmptyPlan("Scoop is not installed on this machine."),
    };

    private async Task<CleanupPlan> PlanCacheAsync(ScoopLayout layout, CancellationToken ct)
    {
        var cache = layout.Cache;

        if (NothingToPlanFor(cache, $"Scoop has not downloaded anything into its cache yet ({cache} does not exist).") is { } nothing)
        {
            return nothing;
        }

        // Scoop lists and empties a linked cache through the link, but every survivor this plan names
        // would resolve through it too, which makes §5.6's check of them say nothing.
        if (LongPath.IsReparsePoint(cache))
        {
            return UnexaminedPlan(
                $"Leaving '{cache}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        if (ScoopCacheEntries.Of(cache, ct) is not { } entries)
        {
            return EmptyPlan(UnreadableRoot.WhyNothingWasPlanned(cache)) with { HasUnreadableRoot = true };
        }

        if (entries.Unrecognised.Count > 0)
        {
            return UnexaminedPlan(Unrecognised(cache, entries.Unrecognised)) with
            {
                ProtectedPaths = BuildProtectedPaths(layout, entries.Unrecognised),
            };
        }

        if (entries.Recognised.Count == 0)
        {
            return EmptyPlan($"Scoop's download cache at {cache} is empty.");
        }

        var measured = await MeasureAllAsync([cache], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Scoop's download cache is {cache}. Scoop's own command removes every download in it "
                + "and leaves the folder, your installed apps and their persisted data."),
        };

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(layout.Command, Arguments, "Clear the Scoop download cache using Scoop's own command")
                {
                    // Each download is removed on its own and is complete or absent, so a run ended
                    // part-way leaves fewer downloads, never a damaged one.
                    WhenCancelled = CancelledCommand.Ended,
                    Estimated = measured.Total,
                    MeasuredPaths = [cache],
                },
            ],
            ProtectedPaths = BuildProtectedPaths(layout, []),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The cache folder heads the list because the command empties it and must not remove it.
    /// The root's own folders are the installed apps, their data, the bucket manifests and the
    /// commands on <c>PATH</c>, which a cache that someone pointed at the root would otherwise sit
    /// beside. The settings files hold credentials.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(ScoopLayout layout, IReadOnlyList<string> unrecognised) => Protect(
    [
        (layout.Cache, "Scoop's cache folder must survive. Only the downloads inside it are removed."),
        .. ScoopToolRoots.RootFolders.Select(folder => (Path.Combine(layout.Root, folder.Name), folder.Holds)),
        .. layout.ConfigFiles.Select(file => (file, "Scoop's settings, which can hold a GitHub token and a proxy password.")),
        .. unrecognised.Select(entry => (entry, "Not a download Scoop made, so it is left alone.")),
    ]);

    private static string Unrecognised(string cache, IReadOnlyList<string> entries)
    {
        var named = string.Join(", ", entries.Take(EntriesNamed).Select(entry => $"'{Path.GetFileName(entry)}'"));
        var rest = entries.Count > EntriesNamed ? $" and {entries.Count - EntriesNamed} more" : string.Empty;
        var count = entries.Count == 1 ? "1 entry" : $"{entries.Count} entries";

        return $"Scoop's download cache at {cache} holds {count} that Scoop did not make: {named}{rest}. "
            + "Scoop's own command removes everything in that folder without looking at it, so Deguffer "
            + "will not run it while they are there. Move them out of the folder and scan again.";
    }

    /// <summary>The layout of the Scoop on <c>PATH</c>, or null where there is none.</summary>
    private ScoopResolution? Resolve() =>
        Environment.FindExecutable("scoop") is { } found
            ? _resolution.Get(() => Judged(ScoopLocations.Resolve(Environment, ScoopCommand.Beside(found))))
            : null;

    /// <summary>
    /// The layout, declined where its root or cache is somewhere no tool's folder may be, such as the
    /// profile, a drive root or Documents. Either can come from a variable or a setting anything on the
    /// machine may have written, and examining the folder as Scoop's would name everything else in it
    /// as Scoop's (see <see cref="ConfiguredFolder"/>). Asked of the defaults as well, which are as
    /// Scoop's install put them and pass.
    /// </summary>
    private ScoopResolution Judged(ScoopResolution resolution)
    {
        if (resolution is not ScoopResolution.Followed { Layout: var layout })
        {
            return resolution;
        }

        var temporary = TempRoots.Resolve(Environment, _system, Volumes).AccountFolders;

        if (ConfiguredFolder.WhyNotOwned(layout.Root, Environment, _system, Volumes, temporary) is { } root)
        {
            return new ScoopResolution.RootDeclined(layout, root);
        }

        return ConfiguredFolder.WhyNotOwned(layout.Cache, Environment, _system, Volumes, temporary) is { } cache
            ? new ScoopResolution.CacheDeclined(layout, cache)
            : resolution;
    }
}
