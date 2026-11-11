using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The textures Maxon's Redshift renderer converted to its own format, kept in its disk cache for Cinema
/// 4D, Maya, Houdini, 3ds Max, Katana, Blender and Redshift's own command line.
///
/// <para><b>Tier 1, observed.</b> Maxon: "Since missing caches are automatically recalculated, manually
/// deleting files from the cache directory is also an option". The cost is a conversion of each texture
/// the next time a scene that uses it renders. Redshift trims the cache to its own <b>Texture Cache Max
/// GB</b>, 32 GB by default, so the row is for reclaiming that allowance. The folders it acts on and the
/// program holding them were seen on a Cinebench 2026 installation.</para>
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
/// <para>Where the caches are, why only the numbered folders in <c>Textures</c> are reached, and what
/// beside them is never touched is <see cref="RedshiftCacheLayout"/>.</para>
/// </summary>
public sealed class RedshiftCacheProvider : CleanupProviderBase
{
    private const string HeldReason =
        "A program that renders with Redshift is running, and Maxon says the cache must stay untouched "
        + "while a render is running, so the cache is left alone. Close Cinebench, Cinema 4D, Maya, Houdini, "
        + "3ds Max, Katana, Blender and any Redshift render, then scan again.";

    private const string CacheExploreReason =
        "This is Redshift's cache. Only the numbered folders of converted textures inside its Textures "
        + "folder may be removed, never the mesh cache a running render holds a lock in.";

    private const string TexturesExploreReason =
        "This is the folder Redshift keeps its converted textures in. Only the numbered folders inside it "
        + "may be removed.";

    private static readonly IReadOnlyList<CacheLevel> TexturesLevel = [new CacheLevel("", RedshiftCacheLayout.Buckets)];

    private readonly ISystemDirectories _system;
    private readonly RowDeclarations _declarations;
    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<RedshiftCacheLayout> _layout = new();
    private readonly PassMemo<Examination> _examination = new();

    public RedshiftCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        RowDeclarations? declarations = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _declarations = declarations ?? new RowDeclarations();
        _stillClosed = new RunningProcessCheck(Inspector, RedshiftCacheLayout.ProcessNames);
    }

    public override string Id => "redshift-cache";

    public override string Name => "Redshift texture cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override ProviderBasis Basis => ProviderBasis.Observed;

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

    private Examination Examine(CancellationToken ct) => _examination.Get(() => Examination.Of(Layout, ct));

    /// <summary>Every answer here is configuration or the disk, which can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _layout.Forget();
        _examination.Forget();
        Volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a numbered folder of converted textures is there, or where a cache was found and
    /// left unexamined. A <c>Textures</c> folder with no numbered folder in it is no row.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var examined = Examine(ct);

        return Task.FromResult(examined.Buckets.Count > 0 || examined.LeftSomethingOut || Layout.Notes.Count > 0);
    }

    /// <summary>
    /// §5.2 for Explore. Each Redshift folder and each cache recognises nothing, and each <c>Textures</c>
    /// only its numbered folders. While a host runs, those are refused too.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var running = RedshiftIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. Layout.Caches
                .Where(cache => cache.IsInsideDataFolder)
                .Select(cache => new ToolRoot(cache.Declared.Path, cache.Declared.Reason, static _ => false)),
            .. Layout.Caches.Select(cache => new ToolRoot(cache.Folder, running ? HeldReason : CacheExploreReason, static _ => false)),
            .. Layout.Caches.Select(cache => running
                ? new ToolRoot(cache.Textures, HeldReason, static _ => false)
                : ToolRoot.Of(cache.Textures, TexturesExploreReason, RedshiftCacheLayout.Buckets)),
        ]);
    }

    /// <summary>
    /// Each <c>Textures</c> folder whole. The clean removes only the numbered folders in it, so this names
    /// more than the clean can reach, which only ever makes a duplicate there less likely to be kept.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Layout.Caches.Select(cache => CleanedPlace.Whole(cache.Textures))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;
        var examined = Examine(ct);

        if (examined.Buckets.Count == 0 && !examined.LeftSomethingOut && layout.Notes.Count == 0)
        {
            return EmptyPlan("Redshift has kept no converted textures in this user's account.");
        }

        var notes = new List<PlanNote>(examined.Notes);
        notes.AddRange(layout.Notes);

        var held = RedshiftIsRunning()
            ? examined.Walks.Where(walked => walked.Walk.Targets.Count > 0).Select(walked => walked.Textures).ToList()
            : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left Redshift's cache alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [.. examined.Buckets.Where(_ => held.Count == 0).Select(bucket => bucket with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        List<(string Path, string Reason)> unrecognised = [];

        foreach (var walked in examined.Walks)
        {
            unrecognised.AddRange(await walked.Walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false));
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a held textures folder is named for why it was held rather than as the folder
            // only some of the contents of are removed.
            ProtectedPaths = await ProtectAsync(
                [
                    .. held.Select(path => (Path: path, Reason: HeldReason)),
                    .. examined.Scan.Protected,
                    .. examined.Walks.SelectMany(walked => walked.Walk.Declined),
                    .. unrecognised,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while a host runs, behind a link, or withheld from another computer's disk
            // is something real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examined.LeftSomethingOut || layout.LeftSomethingUnexamined),
            HasUnreadableRoot = examined.Unreadable,
        };
    }

    private bool RedshiftIsRunning() => Inspector.FindRunning(RedshiftCacheLayout.ProcessNames).Count > 0;

    /// <summary>
    /// One planning pass's reading of the disk: each cache's folders down to <c>Textures</c>, and the
    /// numbered folders in each <c>Textures</c>, read once so presence, the plan and Explore agree.
    /// </summary>
    /// <param name="Scan">The declared folders down to each <c>Textures</c>, with what must survive beside them.</param>
    /// <param name="Walks">Each <c>Textures</c> that was reached, with what its walk found.</param>
    /// <param name="Buckets">Every numbered folder found, listed under the cache it is in.</param>
    private sealed record Examination(
        DeclaredLocationScan Scan,
        IReadOnlyList<(string Textures, LevelWalk Walk)> Walks,
        IReadOnlyList<DeletionTarget> Buckets)
    {
        public IEnumerable<PlanNote> Notes => Scan.Notes.Concat(Walks.SelectMany(walked => walked.Walk.Notes));

        /// <summary>A folder declined as a link, or one Windows would not describe or list.</summary>
        public bool LeftSomethingOut =>
            Scan.Declined.Count > 0 || Walks.Any(walked => walked.Walk.Declined.Count > 0) || Unreadable;

        public bool Unreadable => Scan.CouldNotBeReached || Walks.Any(walked => walked.Walk.Unreadable);

        public static Examination Of(RedshiftCacheLayout layout, CancellationToken ct)
        {
            var scan = DeclaredLocations.Examine([.. layout.Caches.Select(cache => cache.Declared)], ct);
            var reached = scan.Targets.Select(target => target.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var walks = new List<(string Textures, LevelWalk Walk)>();
            var buckets = new List<DeletionTarget>();

            // Only a Textures folder the scan reached is walked: one behind a link, or one Windows would not
            // describe, was declined or reported there.
            foreach (var cache in layout.Caches.Where(cache => reached.Contains(cache.Textures)))
            {
                var walk = CacheLevelWalk.Under(TexturesLevel, cache.Textures, ct);
                var heading = LongPath.Display(cache.Folder);

                walks.Add((cache.Textures, walk));
                buckets.AddRange(walk.Targets.Select(bucket => bucket with { Group = heading }));
            }

            return new Examination(scan, walks, buckets);
        }
    }
}
