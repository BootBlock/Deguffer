using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Claude Code's conversations, offered one session at a time: <c>&lt;session&gt;.jsonl</c> in a project
/// folder under <see cref="ClaudeCodeHome.Projects"/>, and the folder of the same name beside it.
///
/// <para><b>For control, not for space.</b> Claude Code deletes a conversation itself once nobody has
/// used it for its retention period, 30 days unless the user changed it, so what is here is a working
/// set on a timer rather than a backlog. Removing a conversation brings its deletion forward, and every
/// item says when it would have gone anyway. What this row gives that nothing else does is the choice:
/// each conversation named by its title, its project and its dates, the ones in use refused, and the ones
/// the user wants kept kept for good.</para>
///
/// <para><b>Tier 3.</b> Nothing re-creates a conversation. It is the record of what was said — the
/// judgement <see cref="CrashDumpProvider"/> made about the record of an event, and §3 names saved
/// sessions among Tier 3's examples.</para>
///
/// <para><b>Liveness.</b> See <see cref="ClaudeCodeConversations"/> for what is refused. The list of
/// running sessions is read again immediately before each removal, and a conversation is held back then
/// if its session is running again or a session is running in its project. Nothing written in the last
/// <see cref="ClaudeCodeSessionRegistry.RecentWindow"/> is offered, whatever the guard on recently
/// changed files is set to, and that floor travels on the plan.</para>
///
/// <para><b>§5.2.</b> Claude Code's folder and <see cref="ClaudeCodeHome.Projects"/> are declared
/// recognising nothing, and each project folder recognising exactly the conversations and session
/// folders this pass offers. The project's memory and anything unrecognised are Tier 4 by
/// construction.</para>
///
/// <para><b>§5.6, scoped to what no other row removes.</b> A session folder whose conversation has gone
/// is <see cref="ClaudeCodeDerivedStateProvider"/>'s to decide about, so it is neither offered nor
/// asserted here.</para>
///
/// <para><b>One of three Tier 3 rows over Claude Code's data, deliberately.</b> With the typed confirmation on,
/// clearing this beside <see cref="ClaudeCodeFileHistoryProvider"/> means typing both names, on the
/// reasoning that split those rows: what each leaves standing, and how each decides something is
/// finished with, differ.</para>
///
/// <para>§5.1 was asked, and the answer is no. Claude Code's own purge removes a whole project's
/// conversations and memory together, and nothing removes one conversation.</para>
/// </summary>
public sealed class ClaudeCodeConversationProvider : CleanupProviderBase
{
    private const string HomeReason =
        "Claude Code's own folder, with your conversations, their memory, your settings and your sign-in in it. "
        + "Only the conversations you choose are removed from inside it.";

    private const string ProjectsReason =
        "Your conversations with Claude Code, one folder per project, with each project's memory. It stays: "
        + "only the conversations you choose are removed from inside it.";

    private const string CredentialsReason = "Your Claude Code sign-in. Deguffer never opens or removes it.";

    private readonly ClaudeCodeProjectsDiscovery _projects;
    private readonly ClaudeCodeSessionRegistry _sessions;

    private Survey? _survey;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    /// <param name="sessions">
    /// Claude Code's list of running sessions, shared with the other providers over Claude Code's folder so
    /// that one planning pass reads it, and asks about each process in it, once.
    /// </param>
    /// <param name="projects">
    /// The walk over Claude Code's project folders, shared with <see cref="ClaudeCodeDerivedStateProvider"/>
    /// so that one planning pass lists them once.
    /// </param>
    public ClaudeCodeConversationProvider(
        IUserEnvironment? environment = null,
        ClaudeCodeSessionRegistry? sessions = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ClaudeCodeProjectsDiscovery? projects = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _projects = projects ?? new ClaudeCodeProjectsDiscovery(Environment);
        _sessions = sessions ?? new ClaudeCodeSessionRegistry(Environment, Inspector);
    }

    public override string Id => "claude-code-conversations";

    public override string Name => "Claude Code conversations";

    public override SafetyTier Tier => SafetyTier.UserData;

    /// <summary>Each conversation is chosen by its title, one at a time.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Those conversations are gone: they cannot be resumed or read again. Claude Code would have deleted each "
        + "of them itself once its retention period ran out, so what you gain is that time, not space for good. Your other "
        + "conversations, each project's memory, your settings and your sign-in are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Claude Code, Anthropic's coding agent",
        Publisher = "Anthropic",
        Purpose = "Claude Code saves every conversation in its own folder, so that you can resume it, and "
            + "deletes each one itself once nobody has used it for 30 days, unless you changed that.",
        Recommendation = "Remove a conversation only when you want it gone before Claude Code deletes it itself. "
            + "Nothing re-creates one, so nothing is ticked for you, and a conversation Claude Code may still be "
            + "using is never offered.",
    };

    /// <summary>§5.3. A running session appends to its conversation continuously.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["claude"];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ClaudeCodeHome.Resolve(Environment) is { } home
            && LongPath.DirectoryMayExist(Path.Combine(home, ClaudeCodeHome.Projects)));

    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= Declare();

    public override void InvalidateCaches()
    {
        _projects.Invalidate();
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
                + "Code's conversations alone.");
        }

        var folder = Path.Combine(home, ClaudeCodeHome.Projects);

        if (ClaudeCodeHome.FirstObstacle(home, folder) is { } obstacle)
        {
            return obstacle.IsLink
                ? UnexaminedPlan($"Leaving '{obstacle.Path}' alone: it is a link to somewhere else, and Deguffer does not look through a link.")
                : UnreadableRootPlan(obstacle.Path);
        }

        if (NothingToPlanFor(folder, "Claude Code has kept no conversations for this user.") is { } nothing)
        {
            return nothing;
        }

        var survey = Look(ct)!;
        var found = survey.Conversations;

        // §5.3's floor travels on the plan as well as into the preview: a session resumed between the two
        // appends to a conversation the preview offered, and the removal has to spare it itself.
        var effective = MinimumAge.Stricter(keep, survey.Floor);

        var (measuredSteps, measured) = await PlanDeletionsAsync(found.Targets, effective, ct).ConfigureAwait(false);
        var (steps, inUse) = WithoutSessionsInUse(measuredSteps);

        IReadOnlyList<(string Path, string Reason)> recent =
        [
            .. found.Recent,
            .. inUse.SelectMany(step => step.Subjects).Select(path => (path, ClaudeCodeClassificationBuilder.RecentReason)),
        ];

        var notes = new List<PlanNote>(found.Notes);

        notes.AddRange(EvidenceNotes(survey, offered: steps.Count, recent: recent.Count));

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

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
            ProtectedPaths =
            [
                .. Protect([
                    .. found.Survivors
                        .Prepend((Path: Path.Combine(home, ".credentials.json"), Reason: CredentialsReason))
                        .Prepend((Path: folder, Reason: ProjectsReason))
                        .Prepend((Path: home, Reason: HomeReason))
                        .DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase),
                ]),

                // Measured during planning, so each was there when the plan was made: the claim
                // CleanupPlan.NarrowedTo makes for a step the user declined.
                .. recent.Select(item => new ProtectedPath(
                    item.Path, item.Reason, PresenceBefore: PathPresence.Present, Withheld: Withholding.TooRecent)),
            ],
            Notes = notes,
            Keep = effective,
            Fallback = measured.Fallback,
            HasUnreadableRoot = found.Unreadable,
            WasNotExamined = steps.Count == 0 && found.Refused,
        };
    }

    /// <summary>
    /// The steps with every session taken out whose measurement found content written within the guard,
    /// and the steps taken out.
    ///
    /// <para><b>A session goes whole or not at all.</b> The classification dates a session folder one level
    /// down, and a subagent appending to its conversation deep inside moves no timestamp there. The
    /// measurement reads every file, so it is what sees that. The removal would spare the recent files and
    /// take the rest, leaving part of a session in use behind, so the whole session is held back
    /// instead.</para>
    /// </summary>
    private static (IReadOnlyList<CleanupStep> Offered, IReadOnlyList<CleanupStep> InUse) WithoutSessionsInUse(
        IReadOnlyList<CleanupStep> steps)
    {
        var sessions = new HashSet<string>(
            steps.Select(step => step.WithheldRecent ? step.Identity?.Key : null).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        if (sessions.Count == 0)
        {
            return (steps, []);
        }

        var inUse = steps.Where(step => step.Identity is { } identity && sessions.Contains(identity.Key)).ToList();

        return ([.. steps.Except(inUse)], inUse);
    }

    private static IEnumerable<PlanNote> EvidenceNotes(Survey survey, int offered, int recent)
    {
        if (!survey.Evidence.Sessions.Complete)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not read Claude Code's list of running sessions, so it offers no conversation: any of "
                + "them could belong to a session that is still running.");
        }

        if (recent > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(recent, "item", "items")} alone that Claude Code "
                + $"wrote to in the last {ClaudeCodeSessionRegistry.RecentWindow.TotalDays:0} days. A session can run for "
                + "days, so a recent conversation is not offered even when nothing lists it as running.");
        }

        if (offered > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                survey.Evidence.RetentionDays is { } days
                    ? $"Claude Code deletes a conversation itself {days} days after it was last used, so "
                      + "each of these would go by the date under Expires anyway. That date is the one your own Claude "
                      + "Code settings set: a project's settings or your organisation's can set another."
                    : "Deguffer could not read how long your Claude Code settings keep a conversation, so it cannot say "
                      + "when Claude Code would delete each of these itself. It is 30 days unless you changed it.");
        }

        if (offered == 0 && recent == 0 && !survey.Conversations.Refused && !survey.Conversations.Unreadable)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                "Claude Code holds no conversation that Deguffer can show has ended.");
        }
    }

    private IReadOnlyList<ToolRoot> Declare()
    {
        if (Look() is not { } survey)
        {
            // Nothing was classified. Where Windows would not describe the way down, the plan names the
            // place, so Explore is told too: the home recognising nothing refuses everything below it.
            return ClaudeCodeHome.Resolve(Environment) is { } home
                && ClaudeCodeHome.FirstObstacle(home, Path.Combine(home, ClaudeCodeHome.Projects)) is { IsLink: false }
                    ? [new ToolRoot(home, HomeReason, static _ => false)]
                    : [];
        }

        var offered = new HashSet<string>(
            survey.Conversations.Targets.Select(target => target.Path),
            StringComparer.OrdinalIgnoreCase);

        return
        [
            new ToolRoot(survey.Home, HomeReason, static _ => false),
            new ToolRoot(Path.Combine(survey.Home, ClaudeCodeHome.Projects), ProjectsReason, static _ => false),
            .. survey.Conversations.Folders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(folder => new ToolRoot(
                    folder,
                    ProjectsReason,
                    name => offered.Contains(Path.Combine(folder, name)))),
        ];
    }

    /// <summary>One look, memoised for the life of a planning pass (G4). The plan and the declaration read it.</summary>
    private Survey? Look(CancellationToken ct = default) => _survey ??= Examine(ct);

    private Survey? Examine(CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(Environment) is not { } home)
        {
            return null;
        }

        var folder = Path.Combine(home, ClaudeCodeHome.Projects);

        if (ClaudeCodeHome.FirstObstacle(home, folder) is not null || !LongPath.DirectoryExists(folder))
        {
            return null;
        }

        // Fixed once, for the reason MinimumAge is an instant rather than a duration: the preview and the
        // clean must agree about what is recent, however long the preview sits on screen.
        var now = DateTime.UtcNow;

        var evidence = new ClaudeCodeConversationEvidence(
            _projects.Look(ct),
            _sessions.Read(ct),
            _sessions,
            now - ClaudeCodeSessionRegistry.RecentWindow,
            ClaudeCodeRetention.Of(home));

        return new Survey(
            home,
            evidence,
            MinimumAge.Within(ClaudeCodeSessionRegistry.RecentWindow, now),
            ClaudeCodeConversations.Classify(evidence, ct));
    }

    /// <param name="Home">Claude Code's folder.</param>
    /// <param name="Evidence">What every conversation was judged against.</param>
    /// <param name="Floor">The cut-off recent conversations were held back by, as a guard the removal applies.</param>
    /// <param name="Conversations">What was decided about each session.</param>
    private sealed record Survey(
        string Home,
        ClaudeCodeConversationEvidence Evidence,
        MinimumAge Floor,
        ClaudeCodeClassification Conversations);
}
