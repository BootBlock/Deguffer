using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The copies Claude Code takes of a file just before it edits it, so that a session can be rewound: one
/// folder per session under <see cref="ClaudeCodeHome.FileHistory"/> in Claude Code's folder (about
/// 105 MB across 6,279 files on the machine this was measured on).
///
/// <para><b>Tier 3.</b> Nothing re-creates a snapshot. It is the record of a file as it was, and removing
/// it takes the undo away from a session that can still be resumed — the judgement
/// <see cref="CrashDumpProvider"/> made about the record of an event.</para>
///
/// <para><b>A session is dated by its folder, never by the snapshots in it.</b> A snapshot keeps the
/// last-write time of the file it copied: 574 of 1,159 sampled on the measured machine were more than an
/// hour older than their folder, reaching back five months. <see cref="DirectoryAge"/> reads the folder's
/// own timestamp, which moves each time a snapshot is added, beside its entries', so no old file can make
/// a session in use read as old. Dating a session by its files would offer this morning's work as stale.</para>
///
/// <para><b>Liveness, on the rules <see cref="ClaudeCodeDerivedStateProvider"/> keeps.</b> A session
/// Claude Code lists as running is refused and asserted. Where the list cannot be read in full, no
/// session is offered. Nothing written in the last <see cref="ClaudeCodeSessionRegistry.RecentWindow"/> is
/// offered either, whatever the guard on recently changed files is set to, and that floor travels on the
/// plan: a snapshot written between the preview and the clean is spared by the removal itself. The list is
/// read again immediately before each session's snapshots are removed, so a session resumed after the
/// preview keeps the snapshots it may still rewind to. See <see cref="ClaudeCodeSessionCheck"/>.</para>
///
/// <para><b>§5.2.</b> Claude Code's folder is declared recognising nothing, and inside
/// <see cref="ClaudeCodeHome.FileHistory"/> only a folder named for a session is recognised. Anything else
/// is Tier 4 by construction.</para>
///
/// <para><b>§5.6, scoped to Claude Code's folder and <see cref="ClaudeCodeHome.FileHistory"/>.</b>
/// Everything else in Claude Code's folder is <see cref="ClaudeCodeDerivedStateProvider"/>'s to assert, and
/// some of it is that provider's to remove, which a plan here that asserted it would read as a
/// failure.</para>
///
/// <para><b>A second Tier 3 row over Claude Code's data, deliberately.</b> With the typed confirmation on,
/// clearing this and <see cref="ClaudeCodeMcpLogProvider"/> in one pass means typing both names. The rows
/// are split on the basis the VS Code rows are: what each must leave standing, and how each decides that
/// something is finished with, differ.</para>
///
/// <para>§5.1 does not apply: Claude Code has no command that removes its rewind snapshots.</para>
/// </summary>
public sealed class ClaudeCodeFileHistoryProvider : CleanupProviderBase
{
    private const string SnapshotsReason =
        "Claude Code's copies of the files one session edited, each taken just before an edit so that the "
        + "session could be rewound. Nothing re-creates them.";

    private const string HomeReason =
        "Claude Code's own folder, with your conversations, their memory, your settings and your sign-in in it. "
        + "Only the rewind snapshots of sessions that are not running are removed from inside it.";

    private const string FolderReason =
        "Claude Code's rewind snapshots, one folder per session. It stays: only the snapshots of sessions that "
        + "are not running, and that nothing has written to this week, are removed.";

    private const string RunningReason =
        "Claude Code lists this session as running, so it may still rewind these files.";

    private const string UnknownRunningReason =
        "Deguffer could not read Claude Code's list of running sessions, so it cannot tell whether this "
        + "session has ended.";

    private readonly ClaudeCodeSessionRegistry _sessions;

    private Survey? _survey;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    /// <param name="sessions">
    /// Claude Code's list of running sessions, shared with <see cref="ClaudeCodeDerivedStateProvider"/> so
    /// that one planning pass reads it, and asks about each process in it, once.
    /// </param>
    public ClaudeCodeFileHistoryProvider(
        IUserEnvironment? environment = null,
        ClaudeCodeSessionRegistry? sessions = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _sessions = sessions ?? new ClaudeCodeSessionRegistry(Environment, Inspector);
    }

    public override string Id => "claude-code-file-history";

    public override string Name => "Claude Code rewind snapshots";

    public override SafetyTier Tier => SafetyTier.UserData;

    /// <summary>Each step is one session's snapshots, offered and dated one session at a time.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Those sessions can no longer rewind the files they edited to how they were before. The files as they are "
        + "now, your conversations and your settings are untouched, and every session takes its own snapshots "
        + "exactly as before.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Claude Code, Anthropic's coding agent",
        Publisher = "Anthropic",
        Purpose = "Before Claude Code edits a file it saves a copy, so that a session can be rewound to how the "
            + "files were. It keeps one folder of these per session in its own folder, and removes a session's "
            + "folder itself only once nothing has written to it for 30 days.",
        Recommendation = "Clear the snapshots of sessions you will not rewind. Nothing re-creates a snapshot, so "
            + "the row stays unticked, and a session that is running, or that anything wrote to this week, is "
            + "never offered.",
    };

    /// <summary>§5.3. A running session adds a snapshot before each edit it makes.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["claude"];

    /// <summary>
    /// Present where Claude Code has a snapshot folder, a link standing in for one included, so that the
    /// plan can say what it declined.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ClaudeCodeHome.Resolve(Environment) is { } home
            && LongPath.DirectoryMayExist(Path.Combine(home, ClaudeCodeHome.FileHistory)));

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: Claude Code's folder, recognising nothing at its own level,
    /// and the snapshot folder, recognising exactly the sessions this pass would offer.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= Declare();

    public override void InvalidateCaches()
    {
        _sessions.Invalidate();
        _survey = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(Environment) is not { } home)
        {
            return EmptyPlan(
                $"{ClaudeCodeHome.ConfigDirectoryVariable} is set to '{ClaudeCodeHome.ConfiguredValue(Environment)}', "
                + "which is not a full path. Deguffer cannot tell which folder that means, so it is leaving Claude "
                + "Code's rewind snapshots alone.");
        }

        var folder = Path.Combine(home, ClaudeCodeHome.FileHistory);

        if (FirstObstacle(home, folder) is { } obstacle)
        {
            return obstacle.IsLink ? LinkedAway(obstacle.Path) : UnreadableRootPlan(obstacle.Path);
        }

        if (NothingToPlanFor(folder, "Claude Code has kept no rewind snapshots for this user.") is { } nothing)
        {
            return nothing;
        }

        var survey = Look(ct)!;
        var snapshots = survey.Snapshots;
        var notes = new List<PlanNote>(snapshots.Notes);

        if (!survey.Sessions.Complete)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not read Claude Code's list of running sessions, so it offers no session's rewind "
                + "snapshots: any of them could belong to a session that is still running."));
        }

        if (snapshots.Recent.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left the snapshots of {ClaudeCodeClassificationBuilder.Count(snapshots.Recent.Count, "session", "sessions")} "
                + $"alone that Claude Code wrote to in the last {ClaudeCodeSessionRegistry.RecentWindow.TotalDays:0} days. "
                + "A session can run for days, so recent snapshots are not offered even when nothing lists their "
                + "session as running."));
        }

        if (snapshots.Targets.Count == 0 && snapshots.Recent.Count == 0 && !snapshots.Refused && !snapshots.Unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Claude Code holds no rewind snapshots that Deguffer can show belong to a session that has ended."));
        }

        // §5.3's floor travels on the plan as well as into the preview. A session resumed between the two
        // writes new snapshots into a folder the preview offered, and the removal has to spare them itself.
        // Each is a copy whose creation time is new, which is what the guard reads.
        // CleanupProviderBase.PlanAsync will not loosen it.
        var effective = MinimumAge.Stricter(keep, survey.Floor);

        var (steps, measured) = await PlanDeletionsAsync(snapshots.Targets, effective, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (snapshots.Targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths =
            [
                .. Protect([
                    .. snapshots.Survivors
                        .Prepend((Path: survey.Home, Reason: HomeReason))
                        .DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase),
                ]),

                // Measured during planning, so each was there when the plan was made: the claim
                // CleanupPlan.NarrowedTo makes for a step the user declined.
                .. snapshots.Recent.Select(item => new ProtectedPath(
                    item.Path, item.Reason, PresenceBefore: PathPresence.Present, Withheld: Withholding.TooRecent)),
            ],
            Notes = notes,
            Keep = effective,
            Fallback = measured.Fallback,
            HasUnreadableRoot = snapshots.Unreadable,
            WasNotExamined = snapshots.Targets.Count == 0 && snapshots.Refused,
        };
    }

    private IReadOnlyList<ToolRoot> Declare()
    {
        if (Look() is not { } survey)
        {
            // Nothing was classified. Where that is because Windows would not describe the folder or
            // the way down to it, the plan names the place, so Explore is told about it too: the home
            // recognising nothing refuses everything below it, snapshots included.
            return ClaudeCodeHome.Resolve(Environment) is { } home
                && FirstObstacle(home, Path.Combine(home, ClaudeCodeHome.FileHistory)) is { IsLink: false }
                    ? [new ToolRoot(home, HomeReason, static _ => false)]
                    : [];
        }

        var offered = new HashSet<string>(
            survey.Snapshots.Targets.Select(target => target.Path),
            StringComparer.OrdinalIgnoreCase);

        return
        [
            new ToolRoot(survey.Home, HomeReason, static _ => false),
            new ToolRoot(survey.Folder, FolderReason, name => offered.Contains(Path.Combine(survey.Folder, name))),
        ];
    }

    /// <summary>
    /// The plan for a folder on the way down to the snapshots that turned out to be a link. Written
    /// once because the home and the folders below it say the same thing about one.
    /// </summary>
    private CleanupPlan LinkedAway(string path) => UnexaminedPlan(
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not look "
        + "through a link.");

    /// <summary>
    /// The first place on the way down to the snapshot folder that stops it, the folder included, or
    /// null where nothing does. The plan, the survey and the declaration all ask this, so none of them
    /// can read a refusal as absence while another names it.
    ///
    /// <para>The home first, then everything below it, and all of it before the folder is probed
    /// for. Probing for the folder resolves through the home, so a link there that Windows declines
    /// to follow would leave the folder reading as unreachable and the link — which Deguffer can see
    /// perfectly well — never named.</para>
    /// </summary>
    private static DerivedPathObstacle? FirstObstacle(string home, string folder)
    {
        switch (LongPath.ProbeDirectory(home, out var isLink))
        {
            case PathPresence.Refused:
                return new DerivedPathObstacle(home, IsLink: false);

            case PathPresence.Present when isLink is true:
                return new DerivedPathObstacle(home, IsLink: true);
        }

        return DerivedPath.FirstObstacleBetween(home, folder);
    }

    /// <summary>
    /// One look at the snapshot folder and the list of running sessions, memoised for the life of a planning
    /// pass (G4). Presence, planning and the declaration all read it.
    /// </summary>
    private Survey? Look(CancellationToken ct = default) => _survey ??= Examine(ct);

    private Survey? Examine(CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(Environment) is not { } home)
        {
            return null;
        }

        var folder = Path.Combine(home, ClaudeCodeHome.FileHistory);

        if (FirstObstacle(home, folder) is not null || !LongPath.DirectoryExists(folder))
        {
            return null;
        }

        var sessions = _sessions.Read(ct);

        // Fixed once, for the reason MinimumAge is an instant rather than a duration: the preview and the
        // clean must agree about which snapshots are recent, however long the preview sits on screen.
        var now = DateTime.UtcNow;

        return new Survey(
            home,
            folder,
            sessions,
            MinimumAge.Within(ClaudeCodeSessionRegistry.RecentWindow, now),
            Classify(folder, sessions, _sessions, now - ClaudeCodeSessionRegistry.RecentWindow, ct));
    }

    private static ClaudeCodeClassification Classify(
        string folder,
        ClaudeCodeSessionList sessions,
        ClaudeCodeSessionRegistry registry,
        DateTime recentSinceUtc,
        CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        if (sorting.Open(folder, FolderReason) is not { } entries)
        {
            return sorting.Build();
        }

        var running = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry is not DirectoryInfo || !ClaudeCodeHome.IsSessionId(entry.Name))
            {
                sorting.Keep(path, ClaudeCodeClassificationBuilder.UnrecognisedReason);
            }
            else if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                sorting.Link(path);
            }
            else if (!sessions.Complete)
            {
                sorting.Refuse(path, UnknownRunningReason);
            }
            else if (sessions.Lists(entry.Name))
            {
                sorting.Refuse(path, RunningReason);
                running++;
            }
            else
            {
                // Not a leftover: a session's snapshots are the record of its files, offered for what they
                // hold. An empty folder among them frees nothing, so it is shown and never chosen.
                sorting.OfferFolderOnceOldEnough(
                    path,
                    SnapshotsReason,
                    recentSinceUtc,
                    isLeftover: false,
                    ClaudeCodeSessionCheck.Ended(registry, entry.Name),
                    ct);
            }
        }

        if (running > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left the snapshots of {ClaudeCodeClassificationBuilder.Count(running, "running Claude Code session", "running Claude Code sessions")} alone.");
        }

        return sorting.Build();
    }

    /// <param name="Home">Claude Code's folder.</param>
    /// <param name="Folder">The snapshot folder inside it.</param>
    /// <param name="Sessions">What the list of running sessions said.</param>
    /// <param name="Floor">
    /// The same cut-off the classification held recent sessions back by, as a guard the removal applies.
    /// </param>
    /// <param name="Snapshots">What was decided about each session's snapshots.</param>
    private sealed record Survey(
        string Home,
        string Folder,
        ClaudeCodeSessionList Sessions,
        MinimumAge Floor,
        ClaudeCodeClassification Snapshots);
}
