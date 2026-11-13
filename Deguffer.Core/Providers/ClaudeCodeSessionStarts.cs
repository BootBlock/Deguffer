namespace Deguffer.Core.Providers;

/// <summary>
/// The folder each session's conversation says it started in, read once for the life of a preview and
/// the clean that follows it (G4), or not at all where an earlier pass read it
/// (<see cref="ClaudeCodeTranscriptAnswers"/>).
///
/// <para>A conversation's start is on its first lines, and a conversation only ever grows, so the answer
/// for a session cannot change while the operation lasts. The clean asks it again for every step it
/// checks, and reading each running session's conversation that often would be reading the same lines
/// for the same answer.</para>
/// </summary>
internal sealed class ClaudeCodeSessionStarts
{
    private readonly ILookup<string, ClaudeCodeTranscript> _transcripts;
    private readonly ClaudeCodeTranscriptAnswers _answers;
    private readonly Dictionary<string, IReadOnlyList<string?>> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="transcripts">Every conversation, by its session's id, as the preview found them.</param>
    /// <param name="answers">Where each conversation's first lines are read.</param>
    public ClaudeCodeSessionStarts(ILookup<string, ClaudeCodeTranscript> transcripts, ClaudeCodeTranscriptAnswers answers)
    {
        _transcripts = transcripts;
        _answers = answers;
    }

    /// <summary>
    /// Where each of the session's conversations says it started, with null for one that does not say or
    /// cannot be read. Empty for a session new enough to have written no conversation when the preview
    /// looked.
    /// </summary>
    public IReadOnlyList<string?> Of(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_read.TryGetValue(sessionId, out var starts))
            {
                _read[sessionId] = starts =
                [
                    .. _transcripts[sessionId].Select(transcript => _answers.Place(transcript, ct)?.Project),
                ];
            }

            return starts;
        }
    }
}
