using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The entries a plan would offer, each as the kind it would offer, for the §5.2 declaration of a
/// project folder.
///
/// <para><b>A title is read only when the entry is asked about.</b> A plan leaves alone a conversation
/// whose title cannot be read, and §7.1 has Explore refuse what a plan holds back. Reading every title
/// to declare that is the cost the declaration exists to avoid, so the one conversation is read when
/// Explore first asks about it or its folder, and the answer is kept for the life of the pass.</para>
/// </summary>
internal sealed class ClaudeCodeOfferedEntries
{
    private readonly Dictionary<string, (ChildKind Kind, ClaudeCodeConversationOffer Offer)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _titled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClaudeCodeTranscriptAnswers _transcripts;

    public ClaudeCodeOfferedEntries(IEnumerable<ClaudeCodeConversationOffer> offers, ClaudeCodeTranscriptAnswers transcripts)
    {
        _transcripts = transcripts;

        foreach (var offer in offers)
        {
            _entries[offer.Transcript.Path] = (ChildKind.File, offer);

            foreach (var (folder, _) in offer.Folders)
            {
                _entries[folder.Path] = (ChildKind.Folder, offer);
            }
        }
    }

    /// <summary>Whether <paramref name="child"/> of <paramref name="folder"/> is an entry a plan would offer, as what it would offer.</summary>
    public bool Recognises(string folder, ToolRootChild child) =>
        _entries.TryGetValue(Path.Combine(folder, child.Name), out var entry)
        && entry.Kind == child.Kind
        && CanBeTitled(entry.Offer);

    private bool CanBeTitled(ClaudeCodeConversationOffer offer) =>
        _titled.GetOrAdd(
            offer.Transcript.Path,
            _ => _transcripts.Describe(offer.Transcript, offer.Placement, CancellationToken.None) is not null);
}
