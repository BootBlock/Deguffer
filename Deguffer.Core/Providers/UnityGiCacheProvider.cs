using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The GI cache the Unity Editor shares between every project on the machine: the intermediate files it
/// writes while it precomputes lighting. It sits outside every project, so
/// <see cref="UnityLibraryProvider"/>, which reaches each project's <c>Library</c>, never sees it.
///
/// <para><b>Tier 2.</b> Everything in it is derived from the scenes, so nothing is lost, but Unity calls
/// clearing it "a last resort": the refill is a lighting bake, which on a large scene takes hours. That
/// is §3's second tier. Unity trims the cache to its own <b>Maximum Cache Size</b>, deleting the oldest
/// files first, so the row is for reclaiming that allowance, which is 10 GB unless the user changed
/// it.</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> Unity's only route is the <b>Clean Cache</b> button
/// in its GI Cache preferences, inside the running Editor, with no command line behind it.</para>
///
/// <para><b>Nothing while an Editor runs (§5.3).</b> Unity says "It's not safe to delete the GI Cache
/// directory manually while the Editor is running", because the Editor keeps references to the files
/// in it. So while <c>Unity</c> is in the process table the cache is left alone, named as a survivor
/// and refused in Explore, and the clean asks again before it removes the cache.</para>
///
/// <para>Where the cache is, why a moved one is named rather than reached, and what beside it is never
/// touched is <see cref="UnityGiCacheLayout"/>.</para>
/// </summary>
public sealed class UnityGiCacheProvider : CleanupProviderBase
{
    private const string HeldReason =
        "The Unity Editor is running, and Unity says deleting the GI cache while the Editor is running is "
        + "not safe, so the cache is left alone. Close every Unity Editor, including one building from "
        + "the command line, then scan again.";

    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<UnityGiCacheSetting> _setting = new();

    public UnityGiCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _stillClosed = new RunningProcessCheck(Inspector, UnityGiCacheLayout.ProcessNames);
    }

    public override string Id => "unity-gi-cache";

    public override string Name => "Unity GI cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next time a scene's lighting is baked, Unity precomputes it from the start. On a large scene "
        + "that can take hours. Your projects, your baked lightmaps, your licence and the build cache every "
        + "project shares are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "the Unity Editor",
        Publisher = "Unity Technologies",
        Purpose = "While Unity precomputes a scene's lighting it keeps the intermediate files in a GI cache "
            + "that every project on the computer shares, so that a later bake can reuse them.",
        Recommendation = "Unity calls clearing the GI cache a last resort, because the next bake starts "
            + "from nothing. Unity's own route is Clean Cache under Preferences, GI Cache. Deguffer removes "
            + "the same folder, and only while every Unity Editor is closed.",
    };

    /// <summary>
    /// Where Unity's settings put the cache, read once per planning pass (G4), so presence, the plan and
    /// Explore's refusals all read the same answer.
    /// </summary>
    private UnityGiCacheSetting Setting => _setting.Get(() => new UnityGiCacheSetting(UnityGiCacheLayout.MovedTo(Environment)));

    public override void InvalidateCaches()
    {
        _setting.Forget();
        base.InvalidateCaches();
    }

    /// <summary>§5.2 for Explore while no Editor runs: Unity's folder recognises nothing, and <c>Caches</c> only the GI cache.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => UnityGiCacheLayout.Roots(Environment);

    /// <summary>
    /// While an Editor runs, the default cache and the folder Unity's settings move it to, each as a root
    /// recognising nothing. A declaration that ages, which is why it is here rather than in
    /// <see cref="ToolRoots"/>. The moved folder is otherwise the user's own, and declared nowhere.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        if (!UnityIsRunning())
        {
            return Task.FromResult<IReadOnlyList<ToolRoot>>([]);
        }

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. new[] { UnityGiCacheLayout.DefaultCacheIn(Environment), Setting.Move?.Folder }
                .OfType<string>()
                .Select(path => new ToolRoot(path, HeldReason, static _ => false)),
        ]);
    }

    /// <summary>
    /// Present where the default cache may hold something, or where Unity's settings moved it, so the
    /// row can say where. A <c>GiCache</c> folder holding nothing is no row: the Editor creates the
    /// folder each time it starts.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DirectoryContent.MayBePresent(UnityGiCacheLayout.DefaultCacheIn(Environment)) || Setting.Move is not null);

    /// <summary>The default cache, the one folder this provider removes. A moved cache is never reached.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([CleanedPlace.Whole(UnityGiCacheLayout.DefaultCacheIn(Environment))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine([UnityGiCacheLayout.Declare(Environment)], ct);
        var move = Setting.Move;

        // A folder the Editor created and never filled is not a cache to offer.
        var found = scan.Targets.Where(target => DirectoryContent.MayBePresent(target.Path)).ToList();

        if (found.Count == 0 && move is null && scan.Declined.Count == 0 && !scan.CouldNotBeReached)
        {
            return EmptyPlan("The Unity Editor has kept no GI cache in this user's account.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        if (move is not null)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, MovedMessage(move)));
        }

        var held = UnityIsRunning() ? found.Select(target => target.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the GI cache alone: {HeldReason}"));
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
            // Held first, so the held cache is named for why it was held rather than as a declared location.
            ProtectedPaths = await ProtectAsync([.. held.Select(path => (Path: path, Reason: HeldReason)), .. scan.Protected], ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while an Editor runs, behind a link, or moved where Deguffer does not reach
            // is something real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0 || move is not null),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    private static string MovedMessage(UnityGiCacheMove move) =>
        (move.Folder is { } folder
            ? $"Unity's GI Cache preferences keep the cache in '{LongPath.Display(folder)}'. "
            : "Unity's GI Cache preferences turn on a custom cache location, and Deguffer could not read which folder it is. ")
        + "Deguffer does not remove a moved GI cache, because nothing in that folder tells Unity's files apart "
        + "from yours. Use Clean Cache under Preferences, GI Cache in the Unity Editor to clear it.";

    private bool UnityIsRunning() => Inspector.FindRunning(UnityGiCacheLayout.ProcessNames).Count > 0;

    /// <summary>One planning pass's reading of Unity's settings, boxed so that "not moved" is remembered too.</summary>
    private sealed record UnityGiCacheSetting(UnityGiCacheMove? Move);
}
