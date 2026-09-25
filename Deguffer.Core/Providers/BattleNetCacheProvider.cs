using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Battle.net launcher's own cache (40.4 MB on the measured machine, in 241 folders named by
/// two hexadecimal digits).
///
/// <para><b>Tier 1, because the cache is keyed by content.</b> Every file in it is named by a
/// 32-digit hash, filed under a folder named by the hash's first two digits. That is the shape of a
/// store of downloads looked up by what they contain, which the launcher fills again from
/// Blizzard's servers when a lookup misses, so nothing is lost when a file is gone.</para>
///
/// <para><b>§5.2, by declaration.</b> The folder beside the cache holds the launcher's account data
/// and a database of what it knows, so this provider names one path and reaches nothing else — the
/// stricter form <see cref="DeclaredLocation"/> gives, where no enumeration exists through which a
/// sibling could be reached. The siblings are then asserted to survive. See
/// <see cref="BattleNetFolder"/>.</para>
///
/// <para>§5.1 has nothing to prefer: the launcher has no command that empties its cache.</para>
///
/// <para><c>ReportsAge</c> is off. The cache's children are hash buckets, so the newest of them is
/// whichever bucket was last written, and §7's column would say "today" about a cache that is
/// mostly old.</para>
/// </summary>
public sealed class BattleNetCacheProvider : CleanupProviderBase
{
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public BattleNetCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _roots =
        [
            BattleNetFolder.Declare(
                Environment,
                new DeclaredLocation(
                    BattleNetFolder.CacheDirectory,
                    "Data the launcher downloaded and saved so it would not fetch it twice. The "
                    + "launcher downloads it again when it is next wanted.",
                    ReportsAge: false)),
        ];
    }

    public override string Id => "battle-net-cache";

    public override string Name => "Battle.net launcher cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The launcher downloads what it had cached again the next time it needs it, so it may "
        + "start more slowly once. Your sign-in, your games and your settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Battle.net desktop launcher",
        Publisher = "Blizzard Entertainment",
        Purpose = "The launcher keeps a cache of data it has downloaded, in its own folder in your "
            + "profile, filed by a hash of each download.",
        Recommendation = "The launcher fetches everything in it again from Blizzard, so it is safe to "
            + "clear. It sits beside the launcher's account data, which is why Deguffer removes only "
            + "the one folder it recognises.",
    };

    /// <summary>
    /// What this provider names. Exposed so tests can assert that the launcher's folder is never a
    /// target and that its siblings are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>§5.3. The running launcher holds files in its cache open.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => BattleNetFolder.ProcessNames;

    /// <summary>§5.2 as §7.1 needs it read from outside. The declaration is the shared one.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => [BattleNetFolder.Root(Environment)];

    /// <summary>
    /// Presence is the cache folder itself being there, not the launcher's folder, which also holds
    /// what this row never removes.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryMayExist));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan("The Battle.net launcher has kept no cache in this user's account.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed.
        if (steps.Count > 0 && BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = scan.NothingWasExamined,
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
