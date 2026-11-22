using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Claude Code session leftovers plan tells the user about the evidence it was judged against,
/// and about what it left alone.
/// </summary>
internal static class ClaudeCodeLeftoverNotes
{
    public static PlanNote NothingLeft { get; } = new(
        PlanNoteSeverity.Information,
        "Claude Code has left nothing behind that Deguffer can show is no longer used.");

    /// <summary>
    /// What the user is told about the two sources of evidence every session-keyed kind depends on,
    /// and about what was held back for being recent.
    /// </summary>
    public static IEnumerable<PlanNote> Evidence(ClaudeCodeEvidence evidence, int recent)
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
}
