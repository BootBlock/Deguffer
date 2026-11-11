namespace Deguffer.Core.Exploring.Links;

/// <summary>A link whose target is not there, or cannot be told to be.</summary>
/// <param name="Path">The link itself, in display form: the shortcut file, or the link's own name.</param>
/// <param name="Target">What it points at, in display form.</param>
/// <param name="State">Never <see cref="LinkTargetState.Present"/>.</param>
public sealed record BrokenLink(string Path, LinkKind Kind, string Target, LinkTargetState State)
{
    /// <summary>
    /// Whether the link may be offered for removal: only where its target is gone. One whose target
    /// cannot be reached is never offered, because the drive or share it leads to may come back.
    /// </summary>
    public bool IsOffered => State is LinkTargetState.Gone;

    /// <summary>
    /// Whether the link is a folder on disk, which a junction always is and a symbolic link is where it
    /// was made to a folder. Removed as the link itself either way.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// The link's own length, as the listing that found it measured it: what a removal of it frees,
    /// carried forward rather than measured again (G5). A link to a folder has none.
    /// </summary>
    public long Bytes { get; init; }
}
