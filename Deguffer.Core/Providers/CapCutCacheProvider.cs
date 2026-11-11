using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches CapCut and Jianying Pro make from the user's media: frame thumbnails, waveforms, the
/// audio extracted for automatic subtitles, and the rest of <see cref="CapCutLayout.CacheChildren"/>.
/// One user measured 54 GB in the subtitle audio alone.
///
/// <para><b>Tier 1, and researched.</b> Each folder is derived from media the draft still names, and
/// the one field test reopened every draft after removing them. Nobody here has seen an installation,
/// so the row is offered and never ticked (<see cref="ProviderBasis"/>).</para>
///
/// <para><b>§5.1 has no route Deguffer can take.</b> CapCut's own <b>Clear cache</b> is a button in its
/// settings with no command behind it, and no source says which folders it empties.</para>
///
/// <para><b>Nothing while the editor runs (§5.3).</b> It reads these folders while a draft is open, so
/// while <c>CapCut</c> or <c>JianyingPro</c> is in the process table its caches are left alone, named
/// as survivors and refused in Explore, and the clean asks again before each folder goes.</para>
///
/// <para>Which folders regenerate, and why the rest of <c>Cache</c> is left alone, is
/// <see cref="CapCutLayout"/>.</para>
/// </summary>
public sealed class CapCutCacheProvider : CleanupProviderBase
{
    private static readonly IReadOnlyList<string> ProcessNames =
        [.. CapCutLayout.Editions.Select(edition => edition.ProcessName)];

    private readonly RunningProcessCheck _stillClosed;

    public CapCutCacheProvider(
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
        _stillClosed = new RunningProcessCheck(Inspector, ProcessNames);
    }

    public override string Id => "capcut-cache";

    public override string Name => "CapCut editing cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next time you open a draft, CapCut draws its thumbnails and waveforms again, which takes a "
        + "moment on a long timeline. Subtitles you already made stay in the draft. Your drafts, the "
        + "effects and music they use, your imported media and your settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.MediaAndCreative,
        Application = "CapCut, a free video editor, and Jianying Pro, its edition for mainland China",
        Publisher = "ByteDance",
        Purpose = "While you edit, CapCut keeps what it works out from your clips so it does not have to "
            + "work it out again: thumbnails of frames, waveforms of audio, and the audio it extracts to "
            + "write automatic subtitles. Users report this growing to tens of gigabytes.",
        Recommendation = "These folders hold nothing your drafts depend on, and CapCut makes them again "
            + "when it needs them. Deguffer leaves the rest of CapCut's cache alone, because some of it "
            + "is effects and media your drafts use. It removes nothing while CapCut is running.",
    };

    public override IReadOnlyList<ToolRoot> ToolRoots =>
        [.. CapCutLayout.Editions.SelectMany(edition => CapCutLayout.CacheRoots(Environment, edition))];

    /// <summary>
    /// While an editor runs, each cache folder this provider would remove, as a root recognising
    /// nothing. A declaration that ages, which is why it is here rather than in <see cref="ToolRoots"/>.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            Running() is not { Count: > 0 } running
                ? []
                : [.. CachedPlaces().Select(path => new ToolRoot(path, HeldReason(running), static _ => false))]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(CapCutLayout.Editions.Any(edition =>
            DirectoryContent.MayBePresent(CapCutLayout.CacheIn(Environment, edition))));

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. CachedPlaces().Select(CleanedPlace.Whole)]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(
            [.. CapCutLayout.Editions.Select(edition => CapCutLayout.Declare(Environment, edition))],
            ct);

        // A folder the editor created and never filled is not a cache to offer.
        var found = scan.Targets.Where(target => DirectoryContent.MayBePresent(target.Path)).ToList();

        if (found.Count == 0 && scan.Declined.Count == 0 && !scan.CouldNotBeReached)
        {
            return EmptyPlan("Neither CapCut nor Jianying Pro has cached anything it makes again in this user's account.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        var running = Running();
        var held = running.Count > 0 ? found.Select(target => target.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the cache alone: {HeldReason(running)}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [.. found.Where(_ => held.Count == 0).Select(target => target with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        var reason = held.Count > 0 ? HeldReason(running) : null;

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a held folder is named for why it was held rather than as a declared location.
            ProtectedPaths = await ProtectAsync(
                [.. held.Select(path => (Path: path, Reason: reason!)), .. scan.Protected],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    private static string HeldReason(IReadOnlyList<string> running) =>
        $"{string.Join(" and ", running)} {(running.Count == 1 ? "is" : "are")} running, and the editor reads "
        + "these folders while a draft is open, so they are left alone. Close it, then scan again.";

    private IReadOnlyList<string> Running() => Inspector.FindRunning(ProcessNames);

    private IEnumerable<string> CachedPlaces() =>
        from edition in CapCutLayout.Editions
        from child in CapCutLayout.CacheChildren
        select Path.Combine(CapCutLayout.CacheIn(Environment, edition), child.Name);
}
