using System.Collections.Concurrent;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <see cref="ClaudeCodeTranscriptReader"/> answered about each transcript, kept from one planning
/// pass to the next while the transcript stays as it was read.
///
/// <para><b>Kept past the operation, deliberately.</b> G4 keeps a filesystem answer for the life of the
/// operation, because a tool's answer can change without notice. A transcript's cannot: its project and
/// start are on its first lines and its title on its last, and none of them changes unless the file is
/// written, which moves its last-write time and, as Claude Code only appends, its length. The listing
/// that found each transcript already holds both, so checking them costs nothing. Without this, every
/// preview read the head and tail of each of the thousands of conversations older than
/// <see cref="ClaudeCodeSessionRegistry.RecentWindow"/>, hundreds of megabytes, for the answers the last
/// preview had.</para>
///
/// <para><b>For the life of the app.</b> Every planning pass and every Explore policy builds its
/// providers again, so the answers are kept here rather than on a provider (<see cref="Shared"/>).</para>
///
/// <para><b>It decides nothing.</b> An answer only names and places a conversation. Which conversations
/// are recent, running or occupied is still decided from the live listing and the live list of running
/// sessions, and §5.6 asserts what is left alone as before.</para>
///
/// <para><b>Only an answer is kept.</b> A read that failed, or found nothing it could use, is read again
/// next time: a locked file is not locked for good, and a conversation that cannot be placed is not
/// offered either way.</para>
///
/// <para><b>Bounded by the listing.</b> <see cref="Retain"/> drops every transcript a listing no longer
/// holds, so what is kept never outgrows the transcripts on the disk.</para>
/// </summary>
/// <param name="reader">Where a transcript is read when nothing is kept for it as it is now.</param>
public sealed class ClaudeCodeTranscriptAnswers(ClaudeCodeTranscriptReader reader)
{
    private readonly ConcurrentDictionary<string, Kept> _kept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The transcripts on the disk, for every pass in the app.</summary>
    public static ClaudeCodeTranscriptAnswers Shared { get; } = new(ClaudeCodeTranscriptReader.Default);

    /// <summary>
    /// Where and when <paramref name="transcript"/>'s session was held. See
    /// <see cref="ClaudeCodeTranscriptReader.Place"/>.
    /// </summary>
    public ClaudeCodePlacement? Place(ClaudeCodeTranscript transcript, CancellationToken ct)
    {
        if (Current(transcript)?.Placement is { } kept)
        {
            return kept;
        }

        var placement = reader.Place(transcript.Path, ct);

        if (placement is not null)
        {
            Keep(transcript, kept => kept with { Placement = placement });
        }

        return placement;
    }

    /// <summary>
    /// <paramref name="transcript"/>'s conversation, placed as <paramref name="placement"/> says. See
    /// <see cref="ClaudeCodeTranscriptReader.Describe"/>.
    /// </summary>
    public ClaudeCodeConversation? Describe(ClaudeCodeTranscript transcript, ClaudeCodePlacement placement, CancellationToken ct)
    {
        if (Current(transcript) is { Titled: true } kept)
        {
            return new ClaudeCodeConversation(placement.Project, placement.Started, kept.Title);
        }

        var conversation = reader.Describe(transcript.Path, placement, ct);

        if (conversation is not null)
        {
            Keep(transcript, kept => kept with { Titled = true, Title = conversation.Title });
        }

        return conversation;
    }

    /// <summary>Forgets every transcript that is not in <paramref name="listed"/>, the transcripts a listing found.</summary>
    public void Retain(IEnumerable<ClaudeCodeTranscript> listed)
    {
        var paths = new HashSet<string>(listed.Select(transcript => transcript.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var path in _kept.Keys)
        {
            if (!paths.Contains(path))
            {
                _kept.TryRemove(path, out _);
            }
        }
    }

    /// <summary>What is kept for <paramref name="transcript"/>, or null where nothing is kept for it as it is now.</summary>
    private Kept? Current(ClaudeCodeTranscript transcript) =>
        _kept.TryGetValue(transcript.Path, out var kept) && kept.Describes(transcript) ? kept : null;

    /// <summary>
    /// Keeps what <paramref name="add"/> adds, beside whatever is already kept for the transcript as it is
    /// now, and in place of anything kept for it as it was before.
    /// </summary>
    private void Keep(ClaudeCodeTranscript transcript, Func<Kept, Kept> add)
    {
        var empty = new Kept(transcript.Length, transcript.LastWrittenUtc, null, false, null);

        _kept.AddOrUpdate(
            transcript.Path,
            static (_, state) => state.add(state.empty),
            static (_, kept, state) => state.add(kept.Describes(state.transcript) ? kept : state.empty),
            (add, empty, transcript));
    }

    /// <param name="Length">The transcript's length when it was listed.</param>
    /// <param name="LastWrittenUtc">When it was last written, when it was listed.</param>
    /// <param name="Placement">Where and when its session was held, or null where it has not been read for that.</param>
    /// <param name="Titled">Whether its title has been read.</param>
    /// <param name="Title">The title read, or null where it has none.</param>
    private sealed record Kept(long Length, DateTime LastWrittenUtc, ClaudeCodePlacement? Placement, bool Titled, string? Title)
    {
        public bool Describes(ClaudeCodeTranscript transcript) =>
            Length == transcript.Length && LastWrittenUtc == transcript.LastWrittenUtc;
    }
}
