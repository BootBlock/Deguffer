using System.Collections.Concurrent;

namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// Reads where a link points and decides whether that is there: <see cref="ILinkTargetReader"/> and
/// <see cref="LinkTargets"/> asked together, once per link, for one operation.
///
/// <para>What each link records is kept for the instance's life, as <see cref="LinkTargets"/> keeps
/// each answer (G4): a name typed into a filter a letter at a time asks again about the same
/// shortcuts, and each is a load through the shell. Safe to share between threads.</para>
/// </summary>
public sealed class LinkInspection(ILinkTargetReader reader, LinkTargets targets)
{
    private readonly ConcurrentDictionary<string, LinkTarget?> _recorded = new(StringComparer.Ordinal);

    /// <summary>
    /// The link at <paramref name="path"/> where its target is gone or cannot be reached, or null where
    /// it is there, or where the link records no target to ask about.
    /// </summary>
    /// <param name="isLink">Whether the entry is a reparse point. See <see cref="ILinkTargetReader.Read"/>.</param>
    /// <param name="isDirectory">Whether the entry is a folder on disk, as the listing said.</param>
    /// <param name="bytes">What the listing measured the entry at. See <see cref="BrokenLink.Bytes"/>.</param>
    public BrokenLink? Inspect(string path, bool isLink, bool isDirectory, long bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_recorded.GetOrAdd(path, static (at, read) => read.Reader.Read(at, read.IsLink), (Reader: reader, IsLink: isLink))
            is not { } link)
        {
            return null;
        }

        var state = targets.StateOf(link.Target);

        return state is LinkTargetState.Present
            ? null
            : new BrokenLink(path, link.Kind, link.Target, state) { IsDirectory = isDirectory, Bytes = bytes };
    }

    /// <summary>
    /// Whether <paramref name="link"/> still leads nowhere, read from the link and the machine again:
    /// asked immediately before it is removed, because a drive plugged in or a program reinstalled
    /// since the list was made has put its target back. A link that now points somewhere else, or no
    /// longer records a target, is not the link that was offered.
    /// </summary>
    public bool IsStillGone(BrokenLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        var now = reader.Read(link.Path, link.Kind is not LinkKind.Shortcut);
        _recorded[link.Path] = now;

        return now is not null
            && now.Kind == link.Kind
            && string.Equals(now.Target, link.Target, StringComparison.OrdinalIgnoreCase)
            && targets.StateNow(now.Target) is LinkTargetState.Gone;
    }
}
