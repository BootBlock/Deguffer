using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Turns the conversations <see cref="ClaudeCodeConversations"/> decided may be offered into what a plan
/// shows: each one's title, its dates and its folders. Only a plan asks, so only a plan reads a title.
///
/// <para><b>A title that cannot be read is a refusal.</b> Where the file has gone or refused the read, or
/// a title in it holds what cannot be read as text, the conversation is left alone with the same reason
/// and the same count as one that cannot be placed. §5.6 asserts it like any other refusal. A conversation
/// that simply has no title is ordinary and is offered.</para>
/// </summary>
internal static class ClaudeCodeConversationOffers
{
    private const string ConversationReason =
        "The conversation itself: every message, tool call and result in it. Nothing re-creates it, and "
        + "Claude Code deletes it itself on the date under Expires.";

    private const string UndatedConversationReason =
        "The conversation itself: every message, tool call and result in it. Nothing re-creates it, and "
        + "Claude Code deletes it itself once nobody has used it for as long as your settings say.";

    private const string SessionFolderReason =
        "The same session's subagent conversations and the tool output it set aside. They go with the "
        + "conversation, and Claude Code deletes them with it.";

    /// <summary>The classification a plan is built from: <paramref name="conversations"/> with their targets.</summary>
    public static ClaudeCodeClassification Describe(
        ClaudeCodeSortedConversations conversations,
        ClaudeCodeConversationEvidence evidence,
        CancellationToken ct)
    {
        var offers = conversations.Offers;
        var described = Titled(offers, evidence.Transcripts, ct);
        var targets = new List<DeletionTarget>(offers.Count * 2);
        var survivors = new List<(string Path, string Reason)>(conversations.Sorted.Survivors);
        var unplaced = conversations.Unplaced;

        foreach (var (offer, conversation) in offers.Zip(described))
        {
            if (conversation is null)
            {
                survivors.Add((offer.Transcript.Path, ClaudeCodeConversations.UnplacedReason));
                survivors.AddRange(offer.Folders.Select(folder => (folder.Folder.Path, ClaudeCodeConversations.UnplacedReason)));
                unplaced++;
            }
            else
            {
                targets.AddRange(Targets(offer, conversation, evidence, conversations.Starts));
            }
        }

        return conversations.Sorted with
        {
            Targets = targets,
            Survivors = survivors,
            Notes = unplaced > 0 ? [.. conversations.Sorted.Notes, UnplacedNote(unplaced)] : conversations.Sorted.Notes,
            Refused = conversations.Sorted.Refused || survivors.Count > conversations.Sorted.Survivors.Count,
        };
    }

    /// <summary>
    /// Every offer's title read at once, bounded (G4), in the offers' order, on threads of the read's own
    /// rather than the pool's: each read blocks its thread on the disk. See <see cref="ListWorkers"/>.
    /// </summary>
    private static ClaudeCodeConversation?[] Titled(
        IReadOnlyList<ClaudeCodeConversationOffer> offers, ClaudeCodeTranscriptAnswers transcripts, CancellationToken ct)
    {
        var described = new ClaudeCodeConversation?[offers.Count];

        ListWorkers.Run(
            offers.Count,
            ClaudeCodeConversations.ReadParallelism,
            index => described[index] = transcripts.Describe(offers[index].Transcript, offers[index].Placement, ct),
            ct);

        return described;
    }

    private static IEnumerable<DeletionTarget> Targets(
        ClaudeCodeConversationOffer offer,
        ClaudeCodeConversation conversation,
        ClaudeCodeConversationEvidence evidence,
        ClaudeCodeSessionStarts starts)
    {
        var transcript = offer.Transcript;
        var identity = new ItemIdentity(transcript.SessionId, Name(conversation));
        var check = ClaudeCodeSessionCheck.Untouched(
            evidence.Registry, starts, transcript.SessionId, conversation.Project, transcript.Path, transcript.LastWrittenUtc);

        IReadOnlyList<ItemFacet> facets =
        [
            new ItemFacet("Title", conversation.Title ?? "Untitled"),
            new ItemFacet("Started", conversation.Started is { } started ? Date(started.UtcDateTime) : "Unknown"),
            new ItemFacet("Expires", Expiry(transcript.LastWrittenUtc, evidence.RetentionDays)),
        ];

        yield return new DeletionTarget(
            transcript.Path,
            evidence.RetentionDays is null ? UndatedConversationReason : ConversationReason,
            transcript.LastWrittenUtc,
            TargetKind.File,
            Identity: identity,
            Facets: facets,
            Group: conversation.Project,
            UseCheck: check);

        foreach (var (folder, written) in offer.Folders)
        {
            yield return new DeletionTarget(
                folder.Path,
                SessionFolderReason,
                written,
                Identity: identity,
                Facets: facets,
                Group: conversation.Project,
                UseCheck: check);
        }
    }

    private static PlanNote UnplacedNote(int unplaced) => new(
        PlanNoteSeverity.Information,
        $"Left {ClaudeCodeClassificationBuilder.Count(unplaced, "conversation", "conversations")} alone that Deguffer "
        + "could not read well enough to say which project each belongs to. A newer version of Claude Code "
        + "may write them differently.");

    /// <summary>What the keep list calls the session: its title, or where and when it was held.</summary>
    private static string Name(ClaudeCodeConversation conversation) =>
        conversation.Title
        ?? $"Untitled conversation in {ProjectName(conversation.Project)}"
           + (conversation.Started is { } started ? $", {Date(started.UtcDateTime)}" : string.Empty);

    private static string ProjectName(string project) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(project)) is { Length: > 0 } name ? name : project;

    /// <summary>When Claude Code would delete a conversation last used at <paramref name="lastUsedUtc"/> itself.</summary>
    private static string Expiry(DateTime lastUsedUtc, long? retentionDays) => retentionDays switch
    {
        null => "Unknown",
        > ClaudeCodeRetention.LongestShownDays => "Not within 100 years",
        { } days => Date(lastUsedUtc.AddDays(days)),
    };

    /// <summary>A day in the user's own time zone, written so that a column of them sorts as text.</summary>
    private static string Date(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
