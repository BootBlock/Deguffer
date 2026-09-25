using Deguffer.Core.Execution;

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
    private readonly Func<ClaudeCodeSessionList, string?> _inUse;

    private ClaudeCodeSessionCheck(ClaudeCodeSessionRegistry sessions, Func<ClaudeCodeSessionList, string?> inUse)
    {
        _sessions = sessions;
        _inUse = inUse;
    }

    /// <summary>For something named by its session: held while the list names that session.</summary>
    public static ClaudeCodeSessionCheck Ended(ClaudeCodeSessionRegistry sessions, string sessionId) =>
        new(sessions, list => list.Lists(sessionId)
            ? "the Claude Code session it belongs to is running again"
            : null);

    /// <summary>
    /// For something that names no session: held while any running session could have written it,
    /// which is <see cref="ClaudeCodeSessionList.Predates"/> answering no.
    /// </summary>
    public static ClaudeCodeSessionCheck Predating(ClaudeCodeSessionRegistry sessions, DateTime writtenUtc) =>
        new(sessions, list => list.Predates(writtenUtc)
            ? null
            : "a Claude Code session that is running now may have made it");

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        var list = _sessions.ReadAfresh(ct);

        return (list.Complete ? _inUse(list) : UnreadableReason) is { } reason
            ? [new InUseNow(step.Path, reason)]
            : [];
    }
}
