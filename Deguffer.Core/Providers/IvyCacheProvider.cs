using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Apache Ivy's download cache, which sbt before 1.3, Ant builds and older Scala tooling resolve
/// dependencies into. Researched rather than measured: no Ivy home was present on the machine this
/// was written against.
///
/// <para><b>§5.2, and it is the Maven finding again.</b> The Ivy home holds two repositories under
/// one root. <c>cache</c> is a copy of what was resolved, and comes back from the repositories a
/// build names. <c>local</c> is where sbt's <c>publishLocal</c> and Ivy's own local resolver publish,
/// and what is there was built on this machine and exists on no remote. Losing it breaks every build
/// that depends on it until somebody publishes it again. Maven's local repository mixes the two in
/// one tree and is Tier 2 for it. Ivy keeps them in sibling folders, so <c>cache</c> alone is
/// recognised at Tier 1 and <c>local</c> is named Tier 4. The <c>.credentials</c> file sbt's
/// documentation puts in the same folder is a repository login.</para>
///
/// <para><b>Where it is.</b> <c>%USERPROFILE%\.ivy2</c>. Only Java properties move it
/// (<c>sbt.ivy.home</c>, <c>ivy.home</c>), set per command or in a launcher's options, which Deguffer
/// cannot read. A moved home is not found, so the failure is a smaller reclaim, never a wrong target.
/// Ivy has no command that clears its cache, so this is the path-based case.</para>
/// </summary>
public sealed class IvyCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The children of the Ivy home this provider classifies. Anything else is Tier 4 by construction.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "cache",
            SafetyTier.RegenerableCache,
            "Dependencies Ivy resolved. It downloads them again when a build next needs them."),
        new ChildClassification(
            "local",
            SafetyTier.DoNotTouch,
            "Artefacts published on this computer with publishLocal, which exist on no remote."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    private const string HomeReason =
        "This is Ivy's own folder. Deguffer removes the cache inside it and nothing else, because the "
        + "artefacts you published locally and your repository login are here too.";

    private readonly RowDeclarations _declarations;

    /// <param name="declarations">
    /// What every row in the planner offers, so a child another row removes from a folder this row
    /// walks is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public IvyCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        RowDeclarations? declarations = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _declarations = declarations ?? new RowDeclarations();
    }

    public override string Id => "ivy";

    public override string Name => "Ivy dependency cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next build that resolves through Ivy downloads its dependencies again, then runs normally. "
        + "What you published locally is untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Apache Ivy, the dependency manager behind older sbt releases and Ant builds",
        Publisher = "the Apache Software Foundation",
        Purpose = "Ivy keeps a copy of every dependency a build resolves through it, in a cache that "
            + "every project on the machine shares. Nothing prunes it.",
        Recommendation = "The cache comes back at the next build. Deguffer never removes the folder "
            + "beside it where sbt's publishLocal puts what you built, because that exists nowhere else.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["java", "sbt", "ant"];

    /// <summary>The Ivy home.</summary>
    public string Home => Path.Combine(Environment.UserProfile, ".ivy2");

    public override IReadOnlyList<ToolRoot> ToolRoots => [ToolRoot.Of(Home, HomeReason, Children)];

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
            [.. Children.DisposableNames.Select(child => CleanedPlace.Whole(Path.Combine(Home, child)))]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(Home));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var home = Home;

        if (NothingToPlanFor(home, $"Ivy has not run for this user — no {home} directory.") is { } nothing)
        {
            return nothing;
        }

        // A home moved to another drive with a junction is the usual reason it is a link. Said once
        // about the whole folder, with no claim about a .credentials that resolves only through it.
        if (LongPath.IsReparsePoint(home))
        {
            return UnexaminedPlan(
                $"Leaving '{home}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, home, ct);

        var spared = await walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false);

        List<PlanNote> notes = [.. walk.Notes, .. spared.Select(CacheLevelWalk.SparedNote)];

        var (steps, measured) = await PlanDeletionsAsync(walk.Targets, keep, ct).ConfigureAwait(false);

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
            Steps = steps,
            ProtectedPaths = Protect(
                walk,
                spared,
                [
                    (home, "The Ivy home itself must survive — only its cache is removed."),
                    (Path.Combine(home, "local"), Children.Classify("local").Reason),
                    (Path.Combine(home, ".credentials"), "A repository login that sbt reads."),
                ]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }
}
