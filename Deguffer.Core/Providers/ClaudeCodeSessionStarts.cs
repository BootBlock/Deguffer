namespace Deguffer.Core.Providers;

/// <summary>
/// The folder each session's conversation says it started in, read once for the life of a preview and
/// the clean that follows it (G4).
///
/// <para>A conversation's start is on its first lines, and a conversation only ever grows, so the answer
/// for a session cannot change while the operation lasts. The clean asks it again for every step it
/// checks, and reading each running session's conversation that often would be reading the same lines
/// for the same answer.</para>
/// </summary>
internal sealed class ClaudeCodeSessionStarts
{
    private readonly ILookup<string, string> _transcripts;
    private readonly Dictionary<string, IReadOnlyList<string?>> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="transcripts">Every conversation's path, by its session's id, as the preview found them.</param>
    public ClaudeCodeSessionStarts(ILookup<string, string> transcripts) => _transcripts = transcripts;

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
                    .. _transcripts[sessionId].Select(path => ClaudeCodeTranscriptReader.Read(path, ct)?.Project),
                ];
            }

            return starts;
        }
    }
}
