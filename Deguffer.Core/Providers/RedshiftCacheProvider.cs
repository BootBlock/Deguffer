using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The textures Maxon's Redshift renderer converted to its own format, kept in its disk cache for Cinema
/// 4D, Maya, Houdini, 3ds Max, Katana, Blender and Redshift's own command line.
///
/// <para><b>Tier 1.</b> Maxon: "Since missing caches are automatically recalculated, manually deleting
/// files from the cache directory is also an option". The cost is a conversion of each texture the next
/// time a scene that uses it renders. Redshift trims the cache to its own <b>Texture Cache Max GB</b>,
/// 32 GB by default, so the row is for reclaiming that allowance.</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> Cinema 4D's <b>Clear Baked Textures</b> is a command
/// inside the running application, and clears one project's.</para>
///
/// <para><b>Nothing while a Redshift host runs (§5.3).</b> Maxon says the cache "must remain untouched
/// only as long as a rendering of the corresponding project is still running", and nothing outside the
/// host says whether one is. So while a program that loads Redshift is in the process table, the cache is
/// left alone, named as a survivor and refused in Explore, and the clean asks again before it removes
/// anything.</para>
///
/// <para>Where the caches are, why only <c>Textures</c> is reached, and what beside it is never touched is
/// <see cref="RedshiftCacheLayout"/>.</para>
/// </summary>
public sealed class RedshiftCacheProvider : CleanupProviderBase
{
    private const string HeldReason =
        "A program that renders with Redshift is running, and Maxon says the cache must stay untouched "
        + "while a render is running, so the cache is left alone. Close Cinema 4D, Maya, Houdini, 3ds Max, "
        + "Katana, Blender and any Redshift render, then scan again.";

    private const string ExploreReason =
        "This is Redshift's cache. Only the converted textures in it may be removed, never the mesh cache "
        + "a running render holds a lock in.";

    private readonly ISystemDirectories _system;
    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<RedshiftCacheLayout> _layout = new();
    private readonly PassMemo<DeclaredLocationScan> _scan = new();

    public RedshiftCacheProvider(
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
        _stillClosed = new RunningProcessCheck(Inspector, RedshiftCacheLayout.ProcessNames);
    }

    public override string Id => "redshift-cache";

    public override string Name => "Redshift texture cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next time a scene renders with Redshift, Redshift converts its textures from your images "
        + "again, so that render starts more slowly once. Your scenes, your images, Redshift's preferences "
        + "and its logs are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.MediaAndCreative,
        Application = "the Redshift renderer, in Cinema 4D, Maya, Houdini, 3ds Max, Katana and Blender",
        Publisher = "Maxon",
        Purpose = "Redshift converts every texture a scene uses into its own format before it renders, and "
            + "keeps the converted files in a cache so that the next render can start at once. It keeps up "
            + "to 32 GB of them unless its preferences say otherwise.",
        Recommendation = "Maxon says deleting files from Redshift's cache is safe, because Redshift makes "
            + "them again. Deguffer removes only the converted textures, and only while every program that "
            + "renders with Redshift is closed.",
    };

    private RedshiftCacheLayout Layout => _layout.Get(() => RedshiftCacheLayout.Find(Environment, _system, Volumes));

    private DeclaredLocationScan Scan(CancellationToken ct) => _scan.Get(() => DeclaredLocations.Examine([.. Layout.Caches.Select(cache => cache.Declared)], ct));

    /// <summary>Every answer here is configuration or the disk, which can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _layout.Forget();
        _scan.Forget();
        Volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a converted texture may be there, or where a cache was found and withheld. A
    /// <c>Textures</c> folder holding nothing is no row: Redshift creates it each time it starts.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var scan = Scan(ct);

        return Task.FromResult(
            scan.Targets.Any(target => DirectoryContent.MayBePresent(target.Path))
            || scan.Declined.Count > 0
            || scan.CouldNotBeReached
            || Layout.Notes.Count > 0);
    }

    /// <summary>
    /// §5.2 for Explore. Each Redshift folder recognises nothing, and each cache only its textures. While
    /// a host runs, the textures are refused too.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var running = RedshiftIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. Layout.Caches
                .Where(cache => cache.IsInsideDataFolder)
                .Select(cache => new ToolRoot(cache.Declared.Path, cache.Declared.Reason, static _ => false)),
            .. Layout.Caches.Select(cache => running
                ? new ToolRoot(cache.Folder, HeldReason, static _ => false)
                : ToolRoot.Folders(
                    cache.Folder,
                    ExploreReason,
                    static name => name.Equals(RedshiftCacheLayout.TexturesFolder, StringComparison.OrdinalIgnoreCase))),
            .. Layout.Caches
                .Where(_ => running)
                .Select(cache => new ToolRoot(cache.Textures, HeldReason, static _ => false)),
        ]);
    }

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Layout.Caches.Select(cache => CleanedPlace.Whole(cache.Textures))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;
        var scan = Scan(ct);

        // A Textures folder Redshift created and never filled is not a cache to offer.
        var found = scan.Targets.Where(target => DirectoryContent.MayBePresent(target.Path)).ToList();

        if (found.Count == 0 && scan.Declined.Count == 0 && !scan.CouldNotBeReached && layout.Notes.Count == 0)
        {
            return EmptyPlan("Redshift has kept no converted textures in this user's account.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        notes.AddRange(layout.Notes);

        var held = RedshiftIsRunning() ? found.Select(target => target.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left Redshift's cache alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [.. found.Where(_ => held.Count == 0).Select(target => target with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

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
            // Held first, so a held textures folder is named for why it was held rather than as the folder
            // only the contents of are removed.
            ProtectedPaths = Protect([.. held.Select(path => (Path: path, Reason: HeldReason)), .. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while a host runs, behind a link, or withheld from another computer's disk
            // is something real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0 || layout.LeftSomethingUnexamined),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    private bool RedshiftIsRunning() => Inspector.FindRunning(RedshiftCacheLayout.ProcessNames).Count > 0;
}
