using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The render cache DaVinci Resolve keeps so a graded or effected timeline plays back in real time:
/// one folder of <c>.dvcc</c> files per project, inside a hidden <c>CacheClip</c> folder.
///
/// <para><b>Tier 2.</b> Resolve renders the cache again as the timeline is played or left idle, from
/// the media and the grade, so nothing is lost. The refill is the render itself, though: the cache
/// exists because those sections play slower than real time, so each second of it costs at least a
/// second of GPU work, and only while the media is online. That is §3's "re-indexing for minutes", as
/// the Unreal shader cache is. Its size follows the cache format the user chose: ProRes 422 HQ at
/// 1080p25 is about 23 MB a second, so a few hundred megabytes is seconds of timeline and a feature is
/// hundreds of gigabytes.</para>
///
/// <para><b>Found, never derived.</b> <see cref="ResolveCacheLayout"/> records where Resolve puts
/// <c>CacheClip</c> without being told, and <see cref="ResolveCacheExamination"/> looks there. A
/// project folder is offered only where it holds a render file. Absence is the ordinary state: the
/// folder is created on the first cache write, and a machine with Resolve installed and nothing
/// cached reports nothing rather than searching further.</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> Resolve's own routes, <b>Playback → Delete Render
/// Cache</b>, its Cache Manager, and in version 20 a preference that deletes cache older than a set
/// number of days, are all inside the running program. Its scripting interface has no call that
/// deletes the cache, and external scripting is limited to the paid edition.</para>
///
/// <para><b>Nothing while Resolve runs (§5.3).</b> Resolve writes the cache while it is open, and
/// keeps running in the background after its window closes, so while <c>Resolve</c> is in the process
/// table every <c>CacheClip</c> is left alone and refused in Explore. The clean asks again before it
/// removes each folder.</para>
/// </summary>
public sealed class ResolveRenderCacheProvider : CleanupProviderBase
{
    private const string HeldReason =
        "DaVinci Resolve is running and writes its render cache while it is open, so the cache is left "
        + "alone. Close Resolve, and check it is not still running in the background, then scan again.";

    private readonly IVolumeInventory _volumes;
    private readonly RunningProcessCheck _stillClosed;
    private ResolveCacheExamination? _examination;

    public ResolveRenderCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _volumes = volumes ?? VolumeInventory.Current;
        _stillClosed = new RunningProcessCheck(Inspector, ResolveCacheLayout.ProcessNames);
    }

    public override string Id => "davinci-resolve-render-cache";

    public override string Name => "DaVinci Resolve render cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Resolve renders the cache again as you play each timeline, so graded and effected sections play "
        + "back slowly until it has, which for a long graded timeline takes hours. It can do so only while "
        + "the media is connected. Your projects, media, optimised media, proxies, backups and recordings "
        + "are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "DaVinci Resolve, the video editor",
        Publisher = "Blackmagic Design",
        Purpose = "Resolve renders graded and effected parts of a timeline ahead of time and keeps them, "
            + "so that playback stays real time. It keeps them in a hidden CacheClip folder in its first "
            + "Media Storage location, which is often the root of a drive.",
        Recommendation = "Resolve can delete its own render cache under Playback, Delete Render Cache. "
            + "Deguffer finds the cache only at the root of a drive or in your Videos folder, so use "
            + "Resolve's own command for a Media Storage location anywhere else.",
    };

    /// <summary>
    /// The one pass both the plan and Explore's refusals read, kept for a planning pass (G4). A pass
    /// that is cancelled throws before it is kept, so the next caller looks again.
    /// </summary>
    private ResolveCacheExamination Examine(CancellationToken ct) =>
        _examination ??= ResolveCacheExamination.Of(ResolveCacheLayout.CandidateFolders(_volumes, Environment), ct);

    public override void InvalidateCaches()
    {
        _examination = null;
        _volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a <c>CacheClip</c> folder is there, or could not be read. A folder holding no
    /// render cache is still a row, which says so, because the user can see the folder.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(!Examine(ct).FoundNothing);

    /// <summary>
    /// §5.2 as §7.1 reads it: each <c>CacheClip</c> recognises only the project folders this pass
    /// proved to hold render files and nothing else, and while Resolve runs, none. Every other path
    /// the plan names as protected, the neighbours beside <c>CacheClip</c> above all, is refused
    /// outright.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);
        var running = ResolveIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. examination.Caches.Select(cache => running
                ? new ToolRoot(cache.Path, HeldReason, static _ => false)
                : new ToolRoot(
                    cache.Path,
                    "This is DaVinci Resolve's cache folder, which also holds its optimised media. Deguffer "
                    + "removes only the project folders in it that hold render cache.",
                    name => cache.RenderFolderNames.Contains(name))),
            .. examination.Survivors
                .Where(survivor => !examination.Caches.Any(cache => cache.Path.Equals(survivor.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(survivor => new ToolRoot(survivor.Path, survivor.Reason, static _ => false)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var examination = Examine(ct);

        if (examination.FoundNothing)
        {
            return EmptyPlan("DaVinci Resolve has left no render cache at the root of any drive or in your Videos folder.");
        }

        var notes = new List<PlanNote>(examination.Notes);
        var held = ResolveIsRunning() ? examination.Caches.Select(cache => cache.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left {string.Join(", ", held.Select(path => $"'{path}'"))} alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examination.RenderFolders
                    .Where(_ => held.Count == 0)
                    .Select(folder => new DeletionTarget(
                        folder.Path,
                        "One project's render cache. Resolve renders it again as the timeline plays.",
                        DirectoryAge.Of(folder.Path, ct),
                        Group: folder.Cache,
                        UseCheck: _stillClosed)),
            ],
            keep,
            ct).ConfigureAwait(false);

        if (steps.Count == 0 && held.Count == 0 && examination.Declined.Count == 0 && !examination.Unreadable)
        {
            return EmptyPlan("No DaVinci Resolve cache folder holds any render cache.");
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
            // Held first, so a held cache folder is named for why it was held rather than as the
            // folder only the render cache inside is taken from.
            ProtectedPaths = Protect(
            [
                .. held.Select(path => (Path: path, Reason: HeldReason))
                    .Concat(examination.Survivors)
                    .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while Resolve runs, or behind a link, is something real left unexamined,
            // so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examination.Declined.Count > 0),
            HasUnreadableRoot = examination.Unreadable,
        };
    }

    private bool ResolveIsRunning() => Inspector.FindRunning(ResolveCacheLayout.ProcessNames).Count > 0;
}
