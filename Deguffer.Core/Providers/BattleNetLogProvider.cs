using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The logs the Battle.net launcher and its built-in browser write about themselves (1.0 MB on the
/// measured machine).
///
/// <para><b>Tier 3, on <see cref="EpicLauncherLogProvider"/>'s reasoning.</b> What is re-created
/// here is the <em>next</em> log, never the ones removed, and somebody in the middle of a support
/// ticket has the only copy. So the row stays unticked, and carries the newest write so the
/// decision stays theirs.</para>
///
/// <para>Separate from <see cref="BattleNetCacheProvider"/> because a plan carries one tier. See
/// <see cref="BattleNetFolder"/>, which holds what both rows must agree about.</para>
///
/// <para>§5.1 does not apply: the launcher offers no way to clear its logs.</para>
/// </summary>
public sealed class BattleNetLogProvider : CleanupProviderBase
{
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public BattleNetLogProvider(
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
                    BattleNetFolder.LogDirectory,
                    "The launcher's own logs and its built-in browser's. The launcher writes a fresh "
                    + "one each time it starts, and the ones removed are not written again.")),
        ];
    }

    public override string Id => "battle-net-logs";

    public override string Name => "Battle.net launcher logs";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The record of every session the launcher has already had is destroyed, so none of it can "
        + "be attached to a support ticket afterwards. The launcher writes a fresh log the next time "
        + "it starts, and nothing about how it runs changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Battle.net desktop launcher",
        Publisher = "Blizzard Entertainment",
        Purpose = "The launcher writes a log, and its built-in browser another, every time it starts. "
            + "Nothing removes the old ones.",
        Recommendation = "A log is the record of a session that will not happen again, and anyone in "
            + "the middle of a support ticket has the only copy here. The row stays unticked and "
            + "shows how recently the folder was written to.",
    };

    /// <summary>
    /// What this provider names. Exposed so tests can assert that the launcher's folder is never a
    /// target and that its siblings are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>§5.3. The running launcher holds the log it is writing open.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => BattleNetFolder.ProcessNames;

    /// <summary>§5.2 as §7.1 needs it read from outside. The declaration is the shared one.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => [BattleNetFolder.Root(Environment)];

    /// <summary>
    /// Presence is the log folder itself being there, not the launcher's folder, which also holds
    /// what this row never removes.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryMayExist));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan("The Battle.net launcher has kept no logs in this user's account.");
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
