using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The disk cache After Effects keeps of rendered preview frames, one per version, in the folder
/// chosen under Preferences, Media &amp; Disk Cache. Adobe's default maximum for it is 10% of the
/// volume, up to 100 GB, so it is one of the largest caches a machine running After Effects holds.
///
/// <para><b>Not the media cache.</b> The media cache Adobe's video and audio applications share holds
/// conformed audio and peak files derived from source media, and has a row of its own. This one holds
/// frames rendered from compositions.</para>
///
/// <para><b>Tier 2.</b> After Effects renders each frame again the next time it is previewed, from
/// the composition and its media, so nothing is lost. The refill is the render itself, though: the
/// cache exists because those frames render slower than real time, which is §3's "re-indexing for
/// minutes", as the DaVinci Resolve render cache is.</para>
///
/// <para><b>Found only where After Effects' own preferences say.</b>
/// <see cref="AfterEffectsCacheSettingsReader"/> reads the folder each version was told to use, and
/// <see cref="AfterEffectsDiskCacheExamination"/> looks below it for the folder After Effects names for
/// this computer. A version whose preferences name no folder is reported and not guessed at: Adobe
/// publishes no default, and a wrong guess is a deletion in a folder the user chose for something
/// else. <see cref="AfterEffectsDiskCacheLayout"/> holds what is recognised and why nothing else
/// is.</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> The routes Adobe documents, Empty Disk Cache under
/// Preferences and Edit, Purge, All Memory &amp; Disk Cache, are inside the running program, and each
/// empties only the running version's cache.</para>
///
/// <para><b>Nothing while After Effects runs (§5.3).</b> It writes the cache while it is open, so while
/// it, or its command-line renderer, is in the process table every cache is left alone and refused in
/// Explore. The clean asks again before it removes each one.</para>
/// </summary>
public sealed class AfterEffectsDiskCacheProvider : CleanupProviderBase, ITemporaryFolderTenant
{
    private const string HeldReason =
        "After Effects is running and writes its disk cache while it is open, so the cache is left alone. "
        + "Close After Effects, and any render it is running, then scan again.";

    private readonly ISystemDirectories _system;
    private readonly RunningProcessCheck _stillClosed;
    private AfterEffectsCacheSettings? _settings;
    private AfterEffectsDiskCacheExamination? _examination;

    public AfterEffectsDiskCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _stillClosed = new RunningProcessCheck(Inspector, AfterEffectsDiskCacheLayout.ProcessNames);
    }

    public override string Id => "after-effects-disk-cache";

    public override string Name => "After Effects disk cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "After Effects renders each frame again the next time you preview it, so previews start slowly "
        + "until it has, which for a heavy composition takes a long time. Your projects, media, "
        + "auto-saves and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Adobe After Effects, the motion graphics and compositing program",
        Publisher = "Adobe",
        Purpose = "After Effects keeps the frames it renders for previews on disk, so a composition it "
            + "has already rendered plays back without rendering again. Each version keeps its own "
            + "cache, in the folder chosen under Preferences, Media & Disk Cache.",
        Recommendation = "After Effects can empty the cache of the version that is open, under "
            + "Preferences, Media & Disk Cache, Empty Disk Cache. Deguffer also finds the caches older "
            + "versions left behind, but only in a folder After Effects' own preferences name.",
    };

    private AfterEffectsCacheSettings Settings => _settings ??= AfterEffectsCacheSettingsReader.Read(Environment.RoamingAppData);

    /// <summary>
    /// The one pass the plan, Explore's refusals and the temporary-folder claim read, kept for a
    /// planning pass (G4). A pass that is cancelled throws before it is kept.
    /// </summary>
    private AfterEffectsDiskCacheExamination Examine(CancellationToken ct) =>
        _examination ??= AfterEffectsDiskCacheExamination.Of(
            Settings.Folders,
            Environment.MachineName,
            TempRoots.Resolve(Environment, _system).Folders,
            ct);

    public override void InvalidateCaches()
    {
        _settings = null;
        _examination = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present once After Effects has kept preferences for this user. Whether a folder they name holds
    /// a cache is the plan's question.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Settings.Found);

    /// <summary>
    /// §5.2 as §7.1 reads it: each version's folder recognises only this computer's cache, and while
    /// After Effects runs, nothing. Every other path the plan names as protected is refused outright.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);
        var running = AfterEffectsIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. examination.Caches.Select(cache => running
                ? new ToolRoot(cache.VersionFolder, HeldReason, static _ => false)
                : new ToolRoot(
                    cache.VersionFolder,
                    "This is After Effects' folder for one version's disk cache. Deguffer removes only the "
                    + "cache named for this computer.",
                    name => name.Equals(examination.CacheName, StringComparison.OrdinalIgnoreCase))),
            .. examination.Survivors
                .Where(survivor => !examination.Caches.Any(cache => cache.VersionFolder.Equals(survivor.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(survivor => new ToolRoot(survivor.Path, survivor.Reason, static _ => false)),
        ]);
    }

    /// <summary>
    /// Each cache inside one of <paramref name="folders"/>, at any depth, spelled from that folder, so
    /// the temporary-files row leaves it to this row whether or not this row would take it today.
    /// </summary>
    public Task<IReadOnlyList<string>> ClaimedEntriesAsync(IReadOnlyList<string> folders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var caches = Examine(ct).Caches;

        return Task.FromResult<IReadOnlyList<string>>(
        [
            .. from folder in folders
               let canonical = LongPath.Unaliased(Path.TrimEndingDirectorySeparator(folder))
               from cache in caches
               let path = LongPath.Unaliased(cache.Path)
               where LongPath.Contains(canonical, path) && !path.Equals(canonical, StringComparison.OrdinalIgnoreCase)
               select Path.Combine(folder, Path.GetRelativePath(canonical, path)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var settings = Settings;

        if (!settings.Found)
        {
            return EmptyPlan("After Effects has not been used by this user.");
        }

        var examination = Examine(ct);
        var notes = new List<PlanNote>();

        notes.AddRange(settings.Unread.Select(path => new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not read After Effects' preferences at '{path}', so a disk cache folder only "
            + "they name was neither cleared nor ruled out.")));

        if (settings.Unset.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"The preferences in {string.Join(", ", settings.Unset.Select(path => $"'{path}'"))} name no "
                + "disk cache folder, so Deguffer did not look for that cache. After Effects then uses a "
                + "default Adobe does not publish, and Deguffer does not guess at it."));
        }

        notes.AddRange(examination.Notes);

        var held = AfterEffectsIsRunning() ? examination.Caches : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left {string.Join(", ", held.Select(cache => $"'{cache.Path}'"))} alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examination.Caches
                    .Where(_ => held.Count == 0)
                    .Select(cache => new DeletionTarget(
                        cache.Path,
                        $"The frames After Effects {Path.GetFileName(cache.VersionFolder)} rendered for "
                        + "previews. It renders them again as you preview.",
                        DirectoryAge.Of(cache.Path, ct),
                        Group: cache.VersionFolder,
                        UseCheck: _stillClosed)),
            ],
            keep,
            ct).ConfigureAwait(false);

        if (steps.Count == 0
            && held.Count == 0
            && examination.Declined.Count == 0
            && settings.Unset.Count == 0
            && !examination.Unreadable
            && settings.IsComplete)
        {
            return EmptyPlan(settings.Folders.Count == 0
                ? "After Effects' preferences name no disk cache folder."
                : "No folder After Effects' preferences name holds a disk cache for this computer.");
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // A held cache is never a survivor otherwise, so the two sets cannot name one path twice.
            ProtectedPaths = Protect(
            [
                .. held.Select(cache => (Path: cache.Path, Reason: HeldReason)),
                .. examination.Survivors,
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while After Effects runs, one behind a link, and one in a folder the
            // preferences do not name are all something real left unexamined.
            WasNotExamined = steps.Count == 0
                && (held.Count > 0 || examination.Declined.Count > 0 || settings.Unset.Count > 0),
            HasUnreadableRoot = examination.Unreadable || !settings.IsComplete,
        };
    }

    private bool AfterEffectsIsRunning() => Inspector.FindRunning(AfterEffectsDiskCacheLayout.ProcessNames).Count > 0;
}
