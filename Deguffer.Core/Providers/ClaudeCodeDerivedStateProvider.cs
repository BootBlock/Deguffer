using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Claude Code leaves behind that nothing reads again: the handshake files of editors that have
/// closed, the messaging keys of processes that have ended, the spilled output and hook environments
/// of sessions whose conversation is already gone, the shell captures of sessions that have ended,
/// and usage events that could not be sent. Under 4 MB on the machine this was measured on, across
/// more than a thousand entries.
///
/// <para><b>It is here for the entries and the tokens, not the bytes.</b> 428 of the environment
/// folders were orphans and every one was empty, and 203 of 217 handshake files named an editor that
/// had closed, each still holding that editor's connection token. A row that led with a number would
/// misdescribe all of that.</para>
///
/// <para><b>Claude Code already cleans up after itself, and this is what the clean-up leaves.</b> It
/// removes a session's folders once their own timestamp is older than its retention period, 30 days
/// unless the user changed it. Everything here is either outside that sweep — the handshake files and
/// keys were measured three months old — or inside the period. §5.1 was asked and the answer is no:
/// Claude Code's own purge removes a whole project's conversations and memory together, and nothing
/// removes these alone.</para>
///
/// <para><b>§5.2: the folder holds your conversations, their memory, your settings and your
/// sign-in.</b> Nothing at its top level is ever a target, and every folder reached into is declared
/// Tier 4 at that level with a reason saying what inside it goes. Each kind of leftover is then
/// recognised by its name and by evidence about the thing that wrote it, and anything else is Tier 4
/// by construction.</para>
///
/// <para><b>Nothing is offered on its name alone.</b> A file naming a process is offered once that
/// process has been asked about and has ended. Anything naming a session is offered only where Claude
/// Code's list of running sessions does not name it — and where that list cannot be read, nothing of
/// that kind is offered at all. On top of that, nothing of that kind, and nothing that names no process
/// at all, is offered if it was written in the last <see cref="ClaudeCodeSessionRegistry.RecentWindow"/>: a session can run for
/// days, and a version of Claude Code older than the list is invisible to it. A file naming a process
/// needs no such floor, because the process it names has been asked about directly.</para>
///
/// <para><b>The clean does not take the preview's word for any of it.</b> The removal keeps anything
/// written after the evidence was read (<see cref="MinimumAge.Since"/>), which is what spares a handshake
/// file an editor started during the preview wrote under a closed one's port. And the list of running
/// sessions is read again immediately before anything named by a session is removed, so a session
/// resumed in the meantime keeps what it left (<see cref="ClaudeCodeSessionCheck"/>).</para>
///
/// <para><b>§5.6, scoped to what no other provider removes.</b> A path this plan asserts and another
/// provider in the same run legitimately deletes reads as a failure, not as an outside removal. So
/// nothing a session provider could take — a conversation, or the output of a session that still has
/// one — is asserted here, and that is stated where the classification is written rather than left to
/// be found.</para>
/// </summary>
public sealed class ClaudeCodeDerivedStateProvider : CleanupProviderBase
{
    /// <summary>
    /// Claude Code's folder, one level down. Every name here is Tier 4: the folders are kept and only
    /// what is recognised inside them goes, and anything the table does not name is Tier 4 by
    /// construction — the direction a folder Claude Code adds in a later release must fail in.
    /// </summary>
    public static readonly DisposableChildSet HomeChildren = new(
    [
        new ChildClassification(
            ClaudeCodeHome.Projects,
            SafetyTier.DoNotTouch,
            "Your conversations with Claude Code, and each project's memory. It stays: only output a session "
            + "spilled beside a conversation that is already gone is removed from inside it by this row. The "
            + "conversations themselves are the Claude Code conversations row's, offered one at a time."),
        new ChildClassification(
            ClaudeCodeHome.Sessions,
            SafetyTier.DoNotTouch,
            "Claude Code's list of running sessions, and the messaging key each running process publishes. It "
            + "stays: only the keys of processes that have ended are removed."),
        new ChildClassification(
            ClaudeCodeHome.Ide,
            SafetyTier.DoNotTouch,
            "The handshake files editors write so that Claude Code can connect to them. It stays: only the files "
            + "of editors that have closed are removed."),
        new ChildClassification(
            ClaudeCodeHome.SessionEnvironments,
            SafetyTier.DoNotTouch,
            "A folder per session for the environment its hooks set. It stays: only the folders of sessions "
            + "that have ended and left no conversation are removed."),
        new ChildClassification(
            ClaudeCodeHome.ShellSnapshots,
            SafetyTier.DoNotTouch,
            "The shell environment Claude Code captures when a session starts a shell. It stays: only the "
            + "captures of sessions that have ended are removed."),
        new ChildClassification(
            ClaudeCodeHome.Telemetry,
            SafetyTier.DoNotTouch,
            "Usage events Claude Code could not send. It stays: only the events of sessions that have ended are "
            + "removed."),
        new ChildClassification(
            ClaudeCodeHome.FileHistory,
            SafetyTier.DoNotTouch,
            "Claude Code's rewind snapshots, one folder per session. This row never removes them: they are the "
            + "Claude Code rewind snapshots row's, offered one session at a time."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> HomeLevel = [new CacheLevel(string.Empty, HomeChildren)];

    /// <summary>
    /// Files in Claude Code's folder whose reason is worth stating. Every file there is asserted to
    /// survive whether or not it is named, because nothing at that level is ever a target.
    /// </summary>
    private static readonly Dictionary<string, string> NamedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [".credentials.json"] = "Your Claude Code sign-in.",
        ["settings.json"] = "Your Claude Code settings.",
        ["CLAUDE.md"] = "Your own instructions to Claude Code, for every project.",
        ["history.jsonl"] = "Every prompt you have typed into Claude Code.",
        [".claude.json"] = "Claude Code's own configuration: your account, and each project's trust decisions and servers.",
        [".last-cleanup"] = "When Claude Code last cleaned up after itself.",
    };

    private const string HomeReason =
        "This is Claude Code's own folder, and your conversations, their memory, your settings and your sign-in "
        + "are in it. Deguffer removes only leftovers it can show nothing reads again, from the Storage page.";

    private const string DeclaredFolderReason =
        "This is inside Claude Code's own folder. Deguffer removes only leftovers it can show nothing reads "
        + "again, from the Storage page.";

    private const string RootFileReason =
        "A file in Claude Code's own folder. Nothing at that level is ever removed.";

    private readonly ClaudeCodeProjectsDiscovery _projects;
    private readonly ClaudeCodeSessionRegistry _sessions;

    private Survey? _survey;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    /// <param name="sessions">
    /// Claude Code's list of running sessions, shared with <see cref="ClaudeCodeFileHistoryProvider"/> and
    /// <see cref="ClaudeCodeConversationProvider"/> so that one planning pass reads it, and asks about each
    /// process in it, once.
    /// </param>
    /// <param name="projects">
    /// The walk over Claude Code's project folders, shared with <see cref="ClaudeCodeConversationProvider"/>
    /// so that one planning pass lists them once.
    /// </param>
    public ClaudeCodeDerivedStateProvider(
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

    public override string Id => "claude-code-leftovers";

    public override string Name => "Claude Code session leftovers";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Nothing you use changes. Your conversations, their memory, your settings and your sign-in stay, and "
        + "each session that starts writes its own handshake files, keys and shell captures exactly as "
        + "before. What goes was left behind by sessions and editors that have already ended.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Claude Code, Anthropic's coding agent, and the editor extensions that run it",
        Publisher = "Anthropic",
        Purpose = "Claude Code keeps each session's working state in one folder in your profile, beside your "
            + "conversations and your settings. It clears old sessions out itself after 30 days, but "
            + "handshake files and keys of processes that have ended are not part of that, and a session "
            + "whose conversation has gone can leave folders behind that nothing refers to.",
        Recommendation = "Deguffer removes only what it can show nothing reads again, once it has checked "
            + "that whatever wrote it has ended. Your conversations, their memory, your settings and your "
            + "sign-in are never offered. It frees very little space: what it tidies is stale files, many "
            + "of them holding a connection token, and empty folders.",
    };

    /// <summary>
    /// §5.3. A running Claude Code holds nothing this plan names open for long, but it writes into the
    /// same folders, and a clean while it runs is worth saying out loud.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["claude"];

    /// <summary>
    /// Present where Claude Code's folder is there, or where <see cref="ClaudeCodeHome.ConfigDirectoryVariable"/>
    /// names something Deguffer cannot use — the plan is where that is said, and an absent provider is
    /// never asked for one.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ClaudeCodeHome.Resolve(Environment) is not { } home || LongPath.DirectoryMayExist(home));

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: Claude Code's folder, recognising nothing at its own
    /// level, and one declaration per folder a leftover is recognised in, recognising exactly what this
    /// pass would offer from it. The two neighbours outside the folder are declared as well, recognising
    /// nothing, because the plan names them as paths that must survive and §7.1 has Explore refuse
    /// every such path.
    /// </summary>
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
                $"{ClaudeCodeHome.WhyUnusable(Environment)} Deguffer is leaving Claude Code's leftovers alone.");
        }

        if (NothingToPlanFor(
                home,
                $"Claude Code has kept no folder for this user — no {home} directory.") is { } nothing)
        {
            return nothing;
        }

        if (LongPath.IsReparsePoint(home))
        {
            return UnexaminedPlan(
                $"Leaving '{home}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        var survey = Look(ct)!;
        var kinds = survey.Kinds;

        var targets = kinds.SelectMany(kind => kind.Targets).ToList();
        var recent = kinds.SelectMany(kind => kind.Recent).ToList();
        var refused = kinds.Any(kind => kind.Refused) || survey.Root.Declined.Count > 0;
        var unreadable = survey.Root.Unreadable || survey.RootEntries is null || kinds.Any(kind => kind.Unreadable);

        var notes = new List<PlanNote>(survey.Root.Notes);
        notes.AddRange(kinds.SelectMany(kind => kind.Notes));
        notes.AddRange(EvidenceNotes(survey.Evidence, recent.Count));

        if (targets.Count == 0 && recent.Count == 0 && !refused && !unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Claude Code has left nothing behind that Deguffer can show is no longer used."));
        }

        // Every offer here is "whatever wrote this has ended", which says nothing about a file written after
        // it was established. An editor that starts during the preview can take a closed one's port and
        // write the same handshake file, so the removal keeps anything written since the evidence was read.
        // CleanupProviderBase.PlanAsync will not loosen it.
        var effective = MinimumAge.Stricter(keep, survey.Looked);

        var (steps, measured) = await PlanDeletionsAsync(targets, effective, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
                .. Protect([.. Survivors(survey).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),

                // Measured during planning, so each was there when the plan was made: the claim
                // CleanupPlan.NarrowedTo makes for a step the user declined.
                .. recent.Select(item => new ProtectedPath(
                    item.Path, item.Reason, PresenceBefore: PathPresence.Present, Withheld: Withholding.TooRecent)),
            ],
            Notes = [.. notes.DistinctBy(note => note.Message, StringComparer.Ordinal)],
            Keep = effective,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && refused,
        };
    }

    /// <summary>
    /// Everything this plan asserts survives: the folder, the two neighbours outside it whose names
    /// begin the same way, every file at its top level, every folder at its top level, and what each
    /// kind of leftover left alone.
    /// </summary>
    private IEnumerable<(string Path, string Reason)> Survivors(Survey survey)
    {
        yield return (survey.Evidence.Home, "Claude Code's own folder must survive — only recognised leftovers inside it are removed.");

        yield return (
            Path.Combine(Environment.UserProfile, ".claude.json"),
            NamedFiles[".claude.json"]);

        yield return (
            Path.Combine(Environment.UserProfile, ".claude-swap-backup"),
            "Not part of Claude Code. Another program keeps saved sign-ins here, under a name that begins the same way.");

        foreach (var file in (survey.RootEntries ?? []).OfType<FileInfo>())
        {
            yield return (LongPath.Display(file.FullName), NamedFiles.GetValueOrDefault(file.Name, RootFileReason));
        }

        foreach (var survivor in survey.Root.Survivors.Concat(survey.Root.Declined))
        {
            yield return survivor;
        }

        foreach (var survivor in survey.Kinds.SelectMany(kind => kind.Survivors))
        {
            yield return survivor;
        }
    }

    /// <summary>
    /// What the user is told about the two sources of evidence every session-keyed kind depends on,
    /// and about what was held back for being recent.
    /// </summary>
    private static IEnumerable<PlanNote> EvidenceNotes(ClaudeCodeEvidence evidence, int recent)
    {
        if (!evidence.Sessions.Complete)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not read Claude Code's list of running sessions, so it offers nothing a running "
                + "session could still be using. Only the handshake files and keys of processes it asked about "
                + "directly can be offered.");
        }

        if (!evidence.Projects.Complete)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not list every Claude Code project folder, so it cannot tell which sessions still "
                + "have a conversation. No session's leftover folders are offered.");
        }

        if (recent > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(recent, "leftover", "leftovers")} alone that Claude Code "
                + $"wrote in the last {ClaudeCodeSessionRegistry.RecentWindow.TotalDays:0} days. A session can run for days, so recent "
                + "leftovers are not offered even when nothing lists their session as running.");
        }
    }

    private IReadOnlyList<ToolRoot> Declare()
    {
        if (Look() is not { } survey)
        {
            // Nothing was classified. Where that is because Windows would not describe the folder, the
            // plan names it, so Explore is told about it too. The folder's own rule recognises none of
            // its children, so it refuses everything below without the per-folder roots a look adds.
            return ClaudeCodeHome.Resolve(Environment) is { } home
                && LongPath.ProbeDirectory(home) is PathPresence.Refused
                    ? [ToolRoot.Of(home, HomeReason, HomeChildren), .. Neighbours()]
                    : [];
        }

        var offered = new HashSet<string>(
            survey.Kinds.SelectMany(kind => kind.Targets).Select(target => target.Path),
            StringComparer.OrdinalIgnoreCase);

        return
        [
            ToolRoot.Of(survey.Evidence.Home, HomeReason, HomeChildren),
            .. Neighbours(),
            .. survey.Kinds
                .SelectMany(kind => kind.Folders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(folder => new ToolRoot(
                    folder,
                    DeclaredFolderReason,
                    name => offered.Contains(Path.Combine(folder, name)))),
        ];
    }

    /// <summary>The two paths beside Claude Code's folder that the plan names as survivors.</summary>
    private IEnumerable<ToolRoot> Neighbours() =>
    [
        new ToolRoot(
            Path.Combine(Environment.UserProfile, ".claude.json"),
            "This is Claude Code's own configuration: your account, and each project's trust decisions "
            + "and servers. Deguffer never removes it.",
            static _ => false),
        new ToolRoot(
            Path.Combine(Environment.UserProfile, ".claude-swap-backup"),
            "This is not part of Claude Code. Another program keeps saved sign-ins here, under a name that "
            + "begins the same way, and Deguffer never removes it.",
            static _ => false),
    ];

    /// <summary>
    /// Everything this pass knows, gathered once (G4). Presence answers from the folder alone, and the
    /// plan and the declaration both read this, so the walk and the process probes behind it are paid
    /// for once per pass and not at all where nothing asks.
    /// </summary>
    private Survey? Look(CancellationToken ct = default) => _survey ??= Examine(ct);

    private Survey? Examine(CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(Environment) is not { } home
            || !LongPath.DirectoryExists(home)
            || LongPath.IsReparsePoint(home))
        {
            return null;
        }

        // Taken before any evidence is read, so everything written after the evidence is written after this.
        var looked = DateTime.UtcNow;

        var evidence = new ClaudeCodeEvidence(
            home,
            _projects.Look(ct),
            _sessions.Read(ct),
            looked - ClaudeCodeSessionRegistry.RecentWindow,
            _sessions);

        return new Survey(
            CacheLevelWalk.Under(HomeLevel, home, ct),
            FolderEntries.Of(home),
            evidence,
            MinimumAge.Since(looked),
            [
                ClaudeCodeSessionFolders.SpilledOutputs(evidence, ct),
                ClaudeCodeSessionFolders.HookEnvironments(evidence, ct),
                ClaudeCodeProcessFiles.EditorLocks(evidence, Inspector, ct),
                ClaudeCodeProcessFiles.MessagingKeys(evidence, Inspector, Environment, ct),
                ClaudeCodeTimedFiles.ShellSnapshots(evidence, ct),
                ClaudeCodeTimedFiles.FailedEvents(evidence, ct),
            ]);
    }

    /// <param name="Root">What Claude Code's folder holds one level down, classified against <see cref="HomeChildren"/>.</param>
    /// <param name="RootEntries">Everything at that level, for the files the walk does not see. Null where it refused to be listed.</param>
    /// <param name="Evidence">What every kind was judged against.</param>
    /// <param name="Looked">
    /// Keeps anything written once the evidence was being read, as a guard the removal applies. See
    /// <see cref="MinimumAge.Since"/>.
    /// </param>
    /// <param name="Kinds">One classification per kind of leftover.</param>
    private sealed record Survey(
        LevelWalk Root,
        IReadOnlyList<FileSystemInfo>? RootEntries,
        ClaudeCodeEvidence Evidence,
        MinimumAge Looked,
        IReadOnlyList<ClaudeCodeClassification> Kinds);
}
