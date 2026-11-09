using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>What the conversations were judged against, gathered once per planning pass.</summary>
/// <param name="Projects">Every project folder, and every conversation and session folder in them.</param>
/// <param name="Sessions">Which sessions are running, and in which projects.</param>
/// <param name="Registry">Where <paramref name="Sessions"/> came from, for the question the clean asks again.</param>
/// <param name="RecentSinceUtc">Anything written at or after this instant is held back.</param>
/// <param name="RetentionDays">
/// How many days Claude Code keeps a conversation nobody uses, or null where Deguffer cannot tell.
/// </param>
/// <param name="Tuning">What the provider's walks run with, under which the session folders are dated.</param>
/// <param name="Reader">Where each conversation's lines are read.</param>
internal sealed record ClaudeCodeConversationEvidence(
    ClaudeCodeProjects Projects,
    ClaudeCodeSessionList Sessions,
    ClaudeCodeSessionRegistry Registry,
    DateTime RecentSinceUtc,
    long? RetentionDays,
    ScanTuner Tuning,
    ClaudeCodeTranscriptReader Reader);

/// <summary>A session every rule has passed, and that may be offered.</summary>
/// <param name="Transcript">The conversation.</param>
/// <param name="Folders">Its folders, each with when it was last written.</param>
/// <param name="Placement">What the conversation's first lines say about it.</param>
internal sealed record ClaudeCodeConversationOffer(
    ClaudeCodeTranscript Transcript,
    IReadOnlyList<(ClaudeCodeSidecar Folder, DateTime Written)> Folders,
    ClaudeCodePlacement Placement);

/// <summary>What was decided about every session, before anything was read for a title.</summary>
/// <param name="Sorted">Everything left alone, and why. It holds no targets: <paramref name="Offers"/> are those.</param>
/// <param name="Offers">Every session that may be offered.</param>
/// <param name="Starts">Where each session started, for the question the clean asks again.</param>
/// <param name="Unplaced">How many conversations were left alone for not saying which project they belong to.</param>
internal sealed record ClaudeCodeSortedConversations(
    ClaudeCodeClassification Sorted,
    IReadOnlyList<ClaudeCodeConversationOffer> Offers,
    ClaudeCodeSessionStarts Starts,
    int Unplaced);

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
///
/// <para><b>Decided from each conversation's first lines.</b> Nothing here reads a title, so Explore's
/// declaration reads only the start of each conversation. A plan reads the titles of what is left, in
/// <see cref="ClaudeCodeConversationOffers"/>, and Explore reads one when it is asked about that
/// conversation (<see cref="ClaudeCodeOfferedEntries"/>).</para>
/// </summary>
internal static class ClaudeCodeConversations
{
    /// <summary>Enough to keep a disk busy while each read stays small. Most reads stop within a few lines.</summary>
    internal const int ReadParallelism = 4;

    internal const string UnplacedReason =
        "Deguffer could not tell which project this conversation belongs to, so it cannot describe it well "
        + "enough for you to choose it.";

    private const string MemoryFolder = "memory";

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

    private const string DuplicateReason =
        "Claude Code keeps more than one conversation under this session's name, so Deguffer cannot tell "
        + "which of them its folders belong to, and leaves them all alone.";

    private const string UnlistedProjectsReason =
        "Deguffer could not list every Claude Code project folder, so it cannot tell which folders belong to "
        + "this session, or whether another conversation shares its name.";

    private const string LinkedPartReason =
        "Part of this session is a link to somewhere else, so Deguffer leaves the whole session alone.";

    public static ClaudeCodeSortedConversations Classify(ClaudeCodeConversationEvidence evidence, CancellationToken ct)
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

        var undated = new List<Session>();
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
                else
                {
                    undated.Add(session);
                }
            }
        }

        // Every session's folders dated in one bounded step, rather than one session's at a time as
        // they are met. See DirectoryAge.OfEach.
        foreach (var (session, lastUsed) in undated.Zip(LastUsed(undated, evidence.Tuning, ct)))
        {
            if (lastUsed is null)
            {
                RefuseAll(sorting, session.Parts, ClaudeCodeClassificationBuilder.UndatedReason);
            }
            else if (lastUsed.Value.Latest >= evidence.RecentSinceUtc)
            {
                foreach (var part in session.Parts)
                {
                    sorting.HoldRecent(part);
                }
            }
            else
            {
                candidates.Add(new Candidate(session, lastUsed.Value.Folders));
            }
        }

        var placed = Placements(candidates, evidence.Reader, ct);
        var starts = new ClaudeCodeSessionStarts(transcripts, evidence.Reader);
        var occupancy = ClaudeCodeOccupancy.Of(evidence.Sessions, starts, ct);
        var offers = new List<ClaudeCodeConversationOffer>(candidates.Count);
        var unplaced = 0;
        var occupied = 0;

        foreach (var (candidate, placement) in candidates.Zip(placed))
        {
            if (placement is null)
            {
                RefuseAll(sorting, candidate.Session.Parts, UnplacedReason);
                unplaced++;
            }
            else if (occupancy.Occupies(placement.Project))
            {
                RefuseAll(sorting, candidate.Session.Parts, OccupiedReason);
                occupied++;
            }
            else
            {
                offers.Add(new ClaudeCodeConversationOffer(candidate.Session.Transcript, candidate.Folders, placement));
            }
        }

        Summarise(sorting, running, occupied, unlisted: !projects.Complete && transcripts.Count > 0);

        return new ClaudeCodeSortedConversations(sorting.Build(), offers, starts, unplaced);
    }

    /// <summary>
    /// When each of <paramref name="sessions"/> was last used, in their order, from its conversation's
    /// own timestamp and each of its folders', or null where a folder would not be listed and so has
    /// no age. Every folder of every session is dated in one step.
    /// </summary>
    private static IReadOnlyList<(DateTime Latest, IReadOnlyList<(ClaudeCodeSidecar Folder, DateTime Written)> Folders)?> LastUsed(
        IReadOnlyList<Session> sessions,
        ScanTuner tuning,
        CancellationToken ct)
    {
        var ages = DirectoryAge.OfEach([.. sessions.SelectMany(session => session.Folders.Select(folder => folder.Path))], tuning, ct);
        var lastUsed = new (DateTime, IReadOnlyList<(ClaudeCodeSidecar, DateTime)>)?[sessions.Count];
        var next = 0;

        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var latest = session.Transcript.LastWrittenUtc;
            var folders = new List<(ClaudeCodeSidecar, DateTime)>(session.Folders.Count);
            var undated = false;

            foreach (var folder in session.Folders)
            {
                if (ages[next++] is not { } written)
                {
                    undated = true;
                    continue;
                }

                folders.Add((folder, written));
                latest = written > latest ? written : latest;
            }

            lastUsed[index] = undated ? null : (latest, folders);
        }

        return lastUsed;
    }

    /// <summary>
    /// Every candidate's conversation placed at once, bounded (G4), in the candidates' order, on threads
    /// of the read's own rather than the pool's: each read blocks its thread on the disk. See
    /// <see cref="ListWorkers"/>.
    /// </summary>
    private static ClaudeCodePlacement?[] Placements(
        List<Candidate> candidates, ClaudeCodeTranscriptReader reader, CancellationToken ct)
    {
        var placed = new ClaudeCodePlacement?[candidates.Count];

        ListWorkers.Run(
            candidates.Count,
            ReadParallelism,
            index => placed[index] = reader.Place(candidates[index].Session.Transcript.Path, ct),
            ct);

        return placed;
    }

    private static void RefuseAll(ClaudeCodeClassificationBuilder sorting, IEnumerable<string> parts, string reason)
    {
        foreach (var part in parts)
        {
            sorting.Refuse(part, reason);
        }
    }

    private static void Summarise(ClaudeCodeClassificationBuilder sorting, int running, int occupied, bool unlisted)
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
