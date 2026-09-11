using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Files Claude Code writes for a session that name no running process: the shell environment it
/// captured, and the usage events it could not send.
///
/// <para><b>A shell capture names no session at all, so the evidence is time.</b> A process cannot
/// write anything before it was created, so a capture written before every running session's process
/// began was written by none of them. That needs every running session's start, and one nobody could
/// read leaves every capture where it is. See <see cref="ClaudeCodeSessionList.Predates"/>.</para>
///
/// <para><b>A failed-events file names its session in its name.</b> One that Claude Code lists as
/// running keeps its file. Claude Code tries to send those events again, and a running session may be
/// adding to the same file.</para>
///
/// <para>Both are then held back for <see cref="ClaudeCodeDerivedStateProvider.RecentWindow"/> as well,
/// for the reason the list alone is not trusted: a version of Claude Code older than the list is
/// invisible to it.</para>
/// </summary>
internal static partial class ClaudeCodeTimedFiles
{
    private const string SnapshotsFolderReason =
        "The shell environment Claude Code captures when a session starts a shell. It stays: only the "
        + "captures of sessions that have ended are removed.";

    private const string FailedEventsFolderReason =
        "Usage events Claude Code could not send. It stays: only the events of sessions that have ended "
        + "are removed.";

    private const string SnapshotReason =
        "The shell environment Claude Code captured for a session that has ended. A session that starts "
        + "later captures its own.";

    private const string FailedEventsReason =
        "Usage events a Claude Code session that has ended could not send to Anthropic. They are Claude "
        + "Code's diagnostics rather than anything of yours.";

    private const string SnapshotInUseReason =
        "A Claude Code session that is still running may have captured this shell, and may still be using it.";

    private const string RunningReason = "Claude Code lists the session this belongs to as running.";

    private const string UnknownRunningReason =
        "Deguffer could not read Claude Code's list of running sessions, so it cannot tell whether the "
        + "session this belongs to has ended.";

    [GeneratedRegex(
        @"\Asnapshot-[a-z0-9]+-[0-9]{13}-[a-z0-9]+\.sh\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotName();

    [GeneratedRegex(
        @"\A1p_failed_events\.(?<session>[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\.[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\.json\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailedEventsName();

    /// <summary>The shell captures of sessions that have ended.</summary>
    public static ClaudeCodeClassification ShellSnapshots(ClaudeCodeEvidence evidence, CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        if (sorting.Open(Path.Combine(evidence.Home, ClaudeCodeHome.ShellSnapshots), SnapshotsFolderReason)
            is not { } entries)
        {
            return sorting.Build();
        }

        var inUse = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (!IsPlainFile(entry) || !SnapshotName().IsMatch(entry.Name))
            {
                sorting.Keep(path, ClaudeCodeClassificationBuilder.UnrecognisedReason);
                continue;
            }

            var written = Written(entry);

            if (!evidence.Sessions.Complete)
            {
                sorting.Refuse(path, UnknownRunningReason);
            }
            else if (!evidence.Sessions.Predates(written))
            {
                sorting.Refuse(path, SnapshotInUseReason);
                inUse++;
            }
            else
            {
                OfferOnceOldEnough(sorting, path, SnapshotReason, written, evidence);
            }
        }

        if (inUse > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(inUse, "shell capture", "shell captures")} alone: "
                + "a Claude Code session that is still running may have made "
                + $"{(inUse == 1 ? "it" : "them")}.");
        }

        return sorting.Build();
    }

    /// <summary>The unsent usage events of sessions that have ended.</summary>
    public static ClaudeCodeClassification FailedEvents(ClaudeCodeEvidence evidence, CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        if (sorting.Open(Path.Combine(evidence.Home, ClaudeCodeHome.Telemetry), FailedEventsFolderReason)
            is not { } entries)
        {
            return sorting.Build();
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (!IsPlainFile(entry) || FailedEventsName().Match(entry.Name) is not { Success: true } match)
            {
                sorting.Keep(path, ClaudeCodeClassificationBuilder.UnrecognisedReason);
            }
            else if (!evidence.Sessions.Complete)
            {
                sorting.Refuse(path, UnknownRunningReason);
            }
            else if (evidence.Sessions.Lists(match.Groups["session"].Value))
            {
                sorting.Refuse(path, RunningReason);
            }
            else
            {
                OfferOnceOldEnough(sorting, path, FailedEventsReason, Written(entry), evidence);
            }
        }

        return sorting.Build();
    }

    private static void OfferOnceOldEnough(
        ClaudeCodeClassificationBuilder sorting,
        string path,
        string reason,
        DateTime written,
        ClaudeCodeEvidence evidence)
    {
        if (written >= evidence.RecentSinceUtc)
        {
            sorting.HoldRecent(path);
        }
        else
        {
            sorting.Offer(new DeletionTarget(path, reason, written, TargetKind.File, IsLeftover: true));
        }
    }

    /// <summary>
    /// The newer of a file's creation and last-write times, asked of <see cref="MinimumAge"/> because
    /// that is where the rule is stated once: a file copied into place keeps the last-write time of its
    /// source, and its creation time is when it arrived.
    /// </summary>
    private static DateTime Written(FileSystemInfo entry) =>
        DateTime.FromFileTimeUtc(MinimumAge.NewestFileTimeOf(entry));

    private static bool IsPlainFile(FileSystemInfo entry) =>
        entry is FileInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
