using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the conversations were judged against, gathered once per planning pass.</summary>
/// <param name="Projects">Every project folder, and every conversation and session folder in them.</param>
/// <param name="Sessions">Which sessions are running, and in which projects.</param>
/// <param name="Registry">Where <paramref name="Sessions"/> came from, for the question the clean asks again.</param>
/// <param name="RecentSinceUtc">Anything written at or after this instant is held back.</param>
/// <param name="RetentionDays">
/// How many days Claude Code keeps a conversation nobody uses, or null where Deguffer cannot tell.
/// </param>
internal sealed record ClaudeCodeConversationEvidence(
    ClaudeCodeProjects Projects,
    ClaudeCodeSessionList Sessions,
    ClaudeCodeSessionRegistry Registry,
    DateTime RecentSinceUtc,
    long? RetentionDays);

/// <summary>
/// Decides which of Claude Code's conversations may be offered, one session at a time.
///
/// <para><b>A session is its conversation and its session folder together.</b> The folder beside
/// <c>&lt;session&gt;.jsonl</c> holds its subagents' conversations and the tool output it spilled, and
/// Claude Code removes it with the conversation. Each is a step of its own, sharing the session's
/// identity, so keeping one keeps both and each can still be left out. A folder whose conversation has
/// gone and which holds only spilled output is then the leftovers row's to offer. Three session folders
/// were measured under a different project folder from their own conversation, so a session's folders
/// are found across every project folder.</para>
///
/// <para><b>Refused, never warned about</b>, and every refusal asserted to survive: a session the list
/// of running sessions names; every session, where that list cannot be read or a project folder cannot
/// be listed; every session of a project a running session is in or was started in, because a running
/// process can resume any of them (see <see cref="ClaudeCodeOccupancy"/>); anything written in the last
/// <see cref="ClaudeCodeSessionRegistry.RecentWindow"/>, because a live conversation is appended to
/// continuously; and a conversation that does not say which project it belongs to, because it cannot be
/// described well enough to choose.</para>
/// </summary>
internal static class ClaudeCodeConversations
{
    /// <summary>Enough to keep a disk busy while each read stays small. Most reads stop within a few lines.</summary>
    private const int ReadParallelism = 4;

    private const string MemoryFolder = "memory";

    private const string ConversationReason =
        "The conversation itself: every message, tool call and result in it. Nothing re-creates it, and "
        + "Claude Code deletes it itself on the date under Expires.";

    private const string UndatedConversationReason =
        "The conversation itself: every message, tool call and result in it. Nothing re-creates it, and "
        + "Claude Code deletes it itself once nobody has used it for as long as your settings say.";

    private const string SessionFolderReason =
        "The same session's subagent conversations and the tool output it set aside. They go with the "
        + "conversation, and Claude Code deletes them with it.";

    private const string ProjectFolderReason =
        "A project folder. Claude Code keeps this project's conversations and memory in it, so only the "
        + "conversations you choose are removed from it.";

    private const string MemoryReason =
        "This project's memory: what Claude Code keeps about the project from one session to the next. "
        + "Claude Code's own clean-up never removes it, and neither does Deguffer.";

    private const string RunningReason = "Claude Code lists this session as running.";

    private const string UnknownRunningReason =
        "Deguffer could not read Claude Code's list of running sessions, so it cannot tell whether this "
        + "session is running.";

    private const string OccupiedReason =
        "Claude Code is running in the project this conversation belongs to, and could resume it.";

    private const string UnplacedReason =
        "Deguffer could not tell which project this conversation belongs to, so it cannot describe it well "
        + "enough for you to choose it.";

    private const string DuplicateReason =
        "Claude Code keeps more than one conversation under this session's name, so Deguffer cannot tell "
        + "which of them its folders belong to, and leaves them all alone.";

    private const string UnlistedProjectsReason =
        "Deguffer could not list every Claude Code project folder, so it cannot tell which folders belong to "
        + "this session, or whether another conversation shares its name.";

    private const string LinkedPartReason =
        "Part of this session is a link to somewhere else, so Deguffer leaves the whole session alone.";

    public static ClaudeCodeClassification Classify(ClaudeCodeConversationEvidence evidence, CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();
        var projects = evidence.Projects;

        foreach (var link in projects.Links)
        {
            sorting.Link(link);
        }

        foreach (var refused in projects.Unreadable)
        {
            sorting.Unlisted(refused, ClaudeCodeClassificationBuilder.UnlistedReason);
        }

        foreach (var unreached in projects.Unreached)
        {
            sorting.Unreached(unreached);
        }

        var folders = projects.Folders
            .SelectMany(folder => folder.Sidecars)
            .ToLookup(sidecar => sidecar.SessionId, StringComparer.OrdinalIgnoreCase);

        var conversations = projects.Folders
            .SelectMany(folder => folder.Transcripts)
            .CountBy(transcript => transcript.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(StringComparer.OrdinalIgnoreCase);

        var transcripts = projects.Folders
            .SelectMany(folder => folder.Transcripts)
            .ToLookup(transcript => transcript.SessionId, transcript => transcript.Path, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<Candidate>();
        var running = 0;

        foreach (var folder in projects.Folders)
        {
            sorting.Folder(folder.Path);
            sorting.Keep(folder.Path, ProjectFolderReason);

            foreach (var other in folder.Others)
            {
                sorting.Keep(
                    LongPath.Display(other.FullName),
                    other.Name.Equals(MemoryFolder, StringComparison.OrdinalIgnoreCase)
                        ? MemoryReason
                        : ClaudeCodeClassificationBuilder.UnrecognisedReason);
            }

            foreach (var transcript in folder.Transcripts)
            {
                ct.ThrowIfCancellationRequested();

                var session = new Session(transcript, [.. folders[transcript.SessionId]]);

                // Which folders are a session's, and whether its name is its own, are both answered across
                // every project folder. A folder that could not be listed may hold the answer.
                if (!projects.Complete)
                {
                    RefuseAll(sorting, session.Parts, UnlistedProjectsReason);
                }
                else if (conversations[transcript.SessionId] > 1)
                {
                    RefuseAll(sorting, session.Parts, DuplicateReason);
                }
                else if (session.Links.Count > 0)
                {
                    foreach (var link in session.Links)
                    {
                        sorting.Link(link);
                    }

                    RefuseAll(sorting, session.Parts.Except(session.Links, StringComparer.OrdinalIgnoreCase), LinkedPartReason);
                }
                else if (!evidence.Sessions.Complete)
                {
                    RefuseAll(sorting, session.Parts, UnknownRunningReason);
                }
                else if (evidence.Sessions.Lists(transcript.SessionId))
                {
                    RefuseAll(sorting, session.Parts, RunningReason);
                    running++;
                }
                else if (LastUsed(session, ct) is not { } lastUsed)
                {
                    RefuseAll(sorting, session.Parts, ClaudeCodeClassificationBuilder.UndatedReason);
                }
                else if (lastUsed.Latest >= evidence.RecentSinceUtc)
                {
                    foreach (var part in session.Parts)
                    {
                        sorting.HoldRecent(part);
                    }
                }
                else
                {
                    candidates.Add(new Candidate(session, lastUsed.Folders));
                }
            }
        }

        var described = Describe(candidates, ct);
        var starts = new ClaudeCodeSessionStarts(transcripts);
        var occupancy = ClaudeCodeOccupancy.Of(evidence.Sessions, starts, ct);
        var unplaced = 0;
        var occupied = 0;

        foreach (var (candidate, conversation) in candidates.Zip(described))
        {
            if (conversation is null)
            {
                RefuseAll(sorting, candidate.Session.Parts, UnplacedReason);
                unplaced++;
            }
            else if (occupancy.Occupies(conversation.Project))
            {
                RefuseAll(sorting, candidate.Session.Parts, OccupiedReason);
                occupied++;
            }
            else
            {
                Offer(sorting, candidate, conversation, evidence, starts);
            }
        }

        Summarise(sorting, running, occupied, unplaced, unlisted: !projects.Complete && transcripts.Count > 0);

        return sorting.Build();
    }

    private static void Offer(
        ClaudeCodeClassificationBuilder sorting,
        Candidate candidate,
        ClaudeCodeConversation conversation,
        ClaudeCodeConversationEvidence evidence,
        ClaudeCodeSessionStarts starts)
    {
        var transcript = candidate.Session.Transcript;
        var identity = new ItemIdentity(transcript.SessionId, Name(conversation));
        var check = ClaudeCodeSessionCheck.Untouched(
            evidence.Registry, starts, transcript.SessionId, conversation.Project, transcript.Path, transcript.LastWrittenUtc);

        IReadOnlyList<ItemFacet> facets =
        [
            new ItemFacet("Title", conversation.Title ?? "Untitled"),
            new ItemFacet("Started", conversation.Started is { } started ? Date(started.UtcDateTime) : "Unknown"),
            new ItemFacet("Expires", Expiry(transcript.LastWrittenUtc, evidence.RetentionDays)),
        ];

        sorting.Offer(new DeletionTarget(
            transcript.Path,
            evidence.RetentionDays is null ? UndatedConversationReason : ConversationReason,
            transcript.LastWrittenUtc,
            TargetKind.File,
            Identity: identity,
            Facets: facets,
            Group: conversation.Project,
            UseCheck: check));

        foreach (var (folder, written) in candidate.Folders)
        {
            sorting.Offer(new DeletionTarget(
                folder.Path,
                SessionFolderReason,
                written,
                Identity: identity,
                Facets: facets,
                Group: conversation.Project,
                UseCheck: check));
        }
    }

    /// <summary>
    /// When the session was last used, from its conversation's own timestamp and each of its folders',
    /// or null where a folder would not be listed and so has no age.
    /// </summary>
    private static (DateTime Latest, IReadOnlyList<(ClaudeCodeSidecar Folder, DateTime Written)> Folders)? LastUsed(
        Session session,
        CancellationToken ct)
    {
        var latest = session.Transcript.LastWrittenUtc;
        var folders = new List<(ClaudeCodeSidecar, DateTime)>(session.Folders.Count);

        foreach (var folder in session.Folders)
        {
            if (DirectoryAge.Of(folder.Path, ct) is not { } written)
            {
                return null;
            }

            folders.Add((folder, written));
            latest = written > latest ? written : latest;
        }

        return (latest, folders);
    }

    /// <summary>Every candidate's conversation read at once, bounded (G4), in the candidates' order.</summary>
    private static ClaudeCodeConversation?[] Describe(List<Candidate> candidates, CancellationToken ct)
    {
        var described = new ClaudeCodeConversation?[candidates.Count];

        Parallel.For(
            0,
            candidates.Count,
            new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = ct },
            index => described[index] = ClaudeCodeTranscriptReader.Read(candidates[index].Session.Transcript.Path, ct));

        return described;
    }

    /// <summary>What the keep list calls the session: its title, or where and when it was held.</summary>
    private static string Name(ClaudeCodeConversation conversation) =>
        conversation.Title
        ?? $"Untitled conversation in {ProjectName(conversation.Project)}"
           + (conversation.Started is { } started ? $", {Date(started.UtcDateTime)}" : string.Empty);

    private static string ProjectName(string project) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(project)) is { Length: > 0 } name ? name : project;

    /// <summary>When Claude Code would delete a conversation last used at <paramref name="lastUsedUtc"/> itself.</summary>
    private static string Expiry(DateTime lastUsedUtc, long? retentionDays) => retentionDays switch
    {
        null => "Unknown",
        > ClaudeCodeRetention.LongestShownDays => "Not within 100 years",
        { } days => Date(lastUsedUtc.AddDays(days)),
    };

    /// <summary>A day in the user's own time zone, written so that a column of them sorts as text.</summary>
    private static string Date(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void RefuseAll(ClaudeCodeClassificationBuilder sorting, IEnumerable<string> parts, string reason)
    {
        foreach (var part in parts)
        {
            sorting.Refuse(part, reason);
        }
    }

    private static void Summarise(
        ClaudeCodeClassificationBuilder sorting, int running, int occupied, int unplaced, bool unlisted)
    {
        if (running > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(running, "running Claude Code session", "running Claude Code sessions")} alone.");
        }

        if (occupied > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(occupied, "conversation", "conversations")} alone from projects "
                + "Claude Code is running in. A running session can resume any conversation in its project.");
        }

        if (unlisted)
        {
            sorting.Note(
                PlanNoteSeverity.Warning,
                "Deguffer could not list every Claude Code project folder, so it offers no conversation: it cannot "
                + "tell which folders belong to each session.");
        }

        if (unplaced > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(unplaced, "conversation", "conversations")} alone that Deguffer "
                + "could not read well enough to say which project each belongs to. A newer version of Claude Code "
                + "may write them differently.");
        }
    }

    /// <param name="Transcript">The conversation.</param>
    /// <param name="Folders">Every folder named for the same session, in any project folder.</param>
    private sealed record Session(ClaudeCodeTranscript Transcript, IReadOnlyList<ClaudeCodeSidecar> Folders)
    {
        public IReadOnlyList<string> Parts { get; } = [Transcript.Path, .. Folders.Select(folder => folder.Path)];

        public IReadOnlyList<string> Links { get; } =
        [
            .. Transcript.IsLink ? [Transcript.Path] : Array.Empty<string>(),
            .. Folders.Where(folder => folder.IsLink).Select(folder => folder.Path),
        ];
    }

    /// <param name="Session">A session every rule but the project's has passed.</param>
    /// <param name="Folders">Its folders, each with when it was last written.</param>
    private sealed record Candidate(
        Session Session,
        IReadOnlyList<(ClaudeCodeSidecar Folder, DateTime Written)> Folders);
}
