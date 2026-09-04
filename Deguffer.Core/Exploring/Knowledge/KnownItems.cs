namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// Everything Deguffer knows about the well-known files and folders a size picture puts in front of
/// somebody, gathered from the areas that hold them.
///
/// <para>Split by area rather than kept as one list, because the areas are read and revised
/// separately: what NTFS reserves at the top of a volume is settled and changes when the filesystem
/// does, and what a developer toolchain leaves in a profile changes with the toolchain. One file
/// per area also keeps each within sight of G1's ceiling.</para>
///
/// <para><b>Every entry here is a claim Deguffer makes to its user about their own disk, so every
/// entry is grounded in the vendor's own documentation rather than in recollection.</b> The
/// deletion line is the part that can cost somebody something: it is written to name the supported
/// way to reclaim the space wherever one exists, on §5.1's reasoning that a tool's own eviction
/// beats deleting paths — which is as true of a deletion the reader performs by hand as of one
/// Deguffer performs for them.</para>
///
/// <para>It explains. It permits nothing. <see cref="Acting.ExploreActionPolicy"/> decides what
/// Explore will remove, and it is asked again immediately before anything is deleted; nothing here
/// is read by either.</para>
///
/// <para><b>No entry uses the word "safe", and §7.1 is why.</b> "Explore never classifies. It
/// reports a name and a number. It never says a thing is safe" — so a verdict here says what
/// deleting something <em>costs</em> and what the supported route is, and leaves the judgement where
/// <see cref="Acting.ExploreVerdict.Unclassified"/> already leaves it. The difference is not
/// pedantry: a size picture that blesses a folder is the failure §1 wrote the tier model to
/// prevent.</para>
/// </summary>
public static class KnownItems
{
    /// <summary>
    /// The whole catalogue, built once for the life of the process (G5). Order carries no meaning:
    /// <see cref="ItemGuide"/> keys every entry by where it resolves to.
    /// </summary>
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        .. VolumeRootItems.All,
        .. VolumeItems.All,
        .. WindowsItems.All,
        .. SharedItems.All,
        .. ProfileItems.All,
        .. ToolchainItems.All,
    ];
}
