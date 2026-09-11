using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The folders Claude Code keeps for one session, offered once that session has ended and left no
/// conversation behind: the tool output it spilled beside its transcript, and the folder its hooks'
/// environment went in.
///
/// <para><b>An orphan is a fact about every project folder at once, and about every running
/// session.</b> Measured, one running session had spilled output and no transcript yet, and three
/// session folders sat under a different project folder from their own transcript. A set difference
/// that consulted neither the list of running sessions nor every project folder would have offered
/// all four.</para>
///
/// <para><b>A session that still has a transcript is not this provider's to decide about, in either
/// direction.</b> Whether to remove a conversation is the user's choice about their own history, made
/// at Tier 3. So such a session's spilled output is neither offered nor asserted to survive here.
/// Asserting it would report a §5.6 failure in any run where a provider that does remove sessions
/// takes it: <see cref="PlanVerifier"/> reads a path any step in the run targeted as destroyed by the
/// run, never as removed from outside it. The same holds for a session folder this pass could not
/// show to be an orphan, and for one whose conversation is gone but which holds more than spilled
/// output, such as a subagent's conversation: removing that is the same choice about the user's
/// history.</para>
/// </summary>
internal static class ClaudeCodeSessionFolders
{
    private const string SpilledOutput = "tool-results";

    private const string MemoryFolder = "memory";

    private const string ProjectFolderReason =
        "A project folder. Claude Code keeps this project's conversations and memory in it, so only the "
        + "output of sessions whose conversation is already gone is removed from it.";

    private const string MemoryReason =
        "This project's memory: what Claude Code keeps about the project from one session to the next. "
        + "Claude Code's own clean-up never removes it, and neither does Deguffer.";

    private const string EnvironmentsFolderReason =
        "Claude Code's folder of per-session environments. It stays: only the folders of sessions that "
        + "have ended and left no conversation are removed.";

    private const string ResumableReason =
        "The environment folder of a session that still has a conversation, which can be resumed.";

    private const string SpilledOutputReason =
        "Tool output a Claude Code session spilled beside its conversation. That conversation is gone, "
        + "so nothing refers to this any more.";

    private const string EnvironmentReason =
        "The folder a Claude Code session kept its hooks' environment in. The session has ended and left "
        + "no conversation to resume, so nothing uses this again.";

    private const string RunningReason = "Claude Code lists the session this belongs to as running.";

    private const string UnknownRunningReason =
        "Deguffer could not read Claude Code's list of running sessions, so it cannot tell whether the "
        + "session this belongs to has ended.";

    private const string UnknownTranscriptReason =
        "Deguffer could not list every Claude Code project folder, so it cannot tell whether this "
        + "session still has a conversation.";

    private const string UndatedReason =
        "Deguffer could not tell when Claude Code last wrote to this, so it is left alone.";

    /// <summary>The output sessions spilled beside a conversation that no longer exists.</summary>
    public static ClaudeCodeClassification SpilledOutputs(ClaudeCodeEvidence evidence, CancellationToken ct)
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

        var running = 0;
        var conversations = 0;

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

            foreach (var sidecar in folder.Sidecars)
            {
                ct.ThrowIfCancellationRequested();

                if (projects.TranscriptIds.Contains(sidecar.SessionId) || !projects.Complete)
                {
                    continue;
                }

                if (sidecar.IsLink)
                {
                    sorting.Link(sidecar.Path);
                    continue;
                }

                if (!evidence.Sessions.Complete)
                {
                    sorting.Refuse(sidecar.Path, UnknownRunningReason);
                    continue;
                }

                if (evidence.Sessions.Lists(sidecar.SessionId))
                {
                    sorting.Refuse(sidecar.Path, RunningReason);
                    running++;
                    continue;
                }

                switch (HoldsOnlySpilledOutput(sidecar.Path))
                {
                    case null:
                        sorting.Unlisted(sidecar.Path, ClaudeCodeClassificationBuilder.UnlistedReason);
                        continue;

                    // A subagent's transcript, or anything else Claude Code may put beside the spilled
                    // output, is conversation rather than something derived from one. Not asserted,
                    // for the reason a session that still has a transcript is not.
                    case false:
                        conversations++;
                        continue;
                }

                OfferOnceOldEnough(sorting, sidecar.Path, SpilledOutputReason, evidence, ct);
            }
        }

        if (running > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left the spilled output of {ClaudeCodeClassificationBuilder.Count(running, "running Claude Code session", "running Claude Code sessions")} alone.");
        }

        if (conversations > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(conversations, "session folder", "session folders")} "
                + "alone although the conversation is gone. Each holds more than spilled tool output, such as "
                + "a subagent's conversation, and that is not a leftover for Deguffer to remove.");
        }

        return sorting.Build();
    }

    /// <summary>The environment folders of sessions that have ended and left no conversation.</summary>
    public static ClaudeCodeClassification HookEnvironments(ClaudeCodeEvidence evidence, CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();
        var folder = Path.Combine(evidence.Home, ClaudeCodeHome.SessionEnvironments);

        if (sorting.Open(folder, EnvironmentsFolderReason) is not { } entries)
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
            else if (evidence.Projects.TranscriptIds.Contains(entry.Name))
            {
                sorting.Keep(path, ResumableReason);
            }
            else if (!evidence.Projects.Complete)
            {
                sorting.Refuse(path, UnknownTranscriptReason);
            }
            else if (!evidence.Sessions.Complete)
            {
                sorting.Refuse(path, UnknownRunningReason);
            }
            else if (evidence.Sessions.Lists(entry.Name))
            {
                sorting.Refuse(path, RunningReason);
                running++;
            }
            else
            {
                OfferOnceOldEnough(sorting, path, EnvironmentReason, evidence, ct);
            }
        }

        if (running > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left the environment folders of {ClaudeCodeClassificationBuilder.Count(running, "running Claude Code session", "running Claude Code sessions")} alone.");
        }

        return sorting.Build();
    }

    /// <summary>
    /// Offer a session folder that has passed every other test, unless something wrote it recently.
    ///
    /// <para><b>Dated by the folder, never by the files deep inside it.</b> <see cref="DirectoryAge"/>
    /// reads the newest of the folder's own timestamp and its immediate entries', and NTFS moves the
    /// folder's own timestamp whenever an entry is added or removed. An old date on a file inside can
    /// therefore never make a folder in use read as old. That matters beside Claude Code's rewind
    /// snapshots, whose files were measured carrying their source file's date, months older than the
    /// folder holding them.</para>
    /// </summary>
    private static void OfferOnceOldEnough(
        ClaudeCodeClassificationBuilder sorting,
        string path,
        string reason,
        ClaudeCodeEvidence evidence,
        CancellationToken ct)
    {
        switch (DirectoryAge.Of(path, ct))
        {
            case null:
                sorting.Refuse(path, UndatedReason);
                break;

            case DateTime written when written >= evidence.RecentSinceUtc:
                sorting.HoldRecent(path);
                break;

            case DateTime written:
                sorting.Offer(new DeletionTarget(path, reason, written, IsLeftover: true));
                break;
        }
    }

    /// <summary>
    /// Whether a session folder holds nothing but spilled tool output, or null where it would not be
    /// listed. An empty folder qualifies: it holds nothing at all.
    /// </summary>
    private static bool? HoldsOnlySpilledOutput(string sidecar) =>
        FolderEntries.Of(sidecar) is { } entries
            ? entries.All(entry =>
                entry is DirectoryInfo
                && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && entry.Name.Equals(SpilledOutput, StringComparison.OrdinalIgnoreCase))
            : null;
}
