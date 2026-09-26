using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Claude Code's list of running sessions, asked again immediately before a leftover the plan offered
/// on its answer is removed.
///
/// <para><b>A session can come back.</b> Resuming one keeps its id, so a session nothing listed at
/// the preview can be listed again by the clean, and it may still rewind to snapshots or read the
/// folders it left. The plan's answer is as old as the preview, and a preview can sit on screen
/// indefinitely.</para>
///
/// <para>The list is read afresh for every question and asked by the rules the plan applied, so the
/// clean holds back nothing the preview would have offered, and offers nothing it would have
/// refused. A list that cannot be read in full holds the step back, as it would have kept the step
/// out of the plan.</para>
/// </summary>
internal sealed class ClaudeCodeSessionCheck : IUseCheck
{
    private const string UnreadableReason =
        "Deguffer could not read Claude Code's list of running sessions, so it cannot tell whether the "
        + "session this belongs to has ended";

    private readonly ClaudeCodeSessionRegistry _sessions;
    private readonly Func<ClaudeCodeSessionList, CancellationToken, string?> _inUse;

    private ClaudeCodeSessionCheck(
        ClaudeCodeSessionRegistry sessions, Func<ClaudeCodeSessionList, CancellationToken, string?> inUse)
    {
        _sessions = sessions;
        _inUse = inUse;
    }

    /// <summary>For something named by its session: held while the list names that session.</summary>
    public static ClaudeCodeSessionCheck Ended(ClaudeCodeSessionRegistry sessions, string sessionId) =>
        new(sessions, (list, _) => list.Lists(sessionId)
            ? "the Claude Code session it belongs to is running again"
            : null);

    /// <summary>
    /// For both parts of a session whose conversation was offered: held while the list names the session,
    /// while a running session may be working in its project (see <see cref="ClaudeCodeOccupancy"/>), or
    /// once anything has written to the conversation since the preview.
    ///
    /// <para><b>The last is what keeps a session whole.</b> The guard on recent content spares a
    /// conversation written to after the preview, and a session folder is a separate step it does not
    /// spare. Asked of both parts, the question holds the folder back with the conversation.</para>
    /// </summary>
    /// <param name="transcripts">Every conversation's path, by its session's id, as the preview found them.</param>
    /// <param name="conversation">The session's conversation.</param>
    /// <param name="lastWrittenUtc">When the preview found the conversation last written.</param>
    public static ClaudeCodeSessionCheck Untouched(
        ClaudeCodeSessionRegistry sessions,
        ILookup<string, string> transcripts,
        string sessionId,
        string project,
        string conversation,
        DateTime lastWrittenUtc) =>
        new(sessions, (list, ct) =>
            list.Lists(sessionId)
                ? "the Claude Code session it belongs to is running again"
                : ClaudeCodeOccupancy.Of(list, transcripts, ct).Occupies(project)
                    ? "Claude Code is running in the project this conversation belongs to, and could resume it"
                    : File.GetLastWriteTimeUtc(LongPath.Extended(conversation)) > lastWrittenUtc
                        ? "Claude Code has written to this conversation since the scan"
                        : null);

    /// <summary>
    /// For something that names no session: held while any running session could have written it,
    /// which is <see cref="ClaudeCodeSessionList.Predates"/> answering no.
    /// </summary>
    public static ClaudeCodeSessionCheck Predating(ClaudeCodeSessionRegistry sessions, DateTime writtenUtc) =>
        new(sessions, (list, _) => list.Predates(writtenUtc)
            ? null
            : "a Claude Code session that is running now may have made it");

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        var list = _sessions.ReadAfresh(ct);

        return (list.Complete ? _inUse(list, ct) : UnreadableReason) is { } reason
            ? [new InUseNow(step.Path, reason)]
            : [];
    }
}
