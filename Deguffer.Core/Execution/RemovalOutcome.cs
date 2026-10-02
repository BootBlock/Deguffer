namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">Bytes of files actually deleted.</param>
/// <param name="Refused">
/// Files left in place because Windows would not release them (§5.3), with their bytes, by reason.
/// </param>
/// <param name="RootRemoved">Whether the target directory itself is gone.</param>
/// <param name="Kept">
/// Files left in place because the user asked for anything touched recently to be left alone.
///
/// Counted apart from <paramref name="Refused"/> rather than added to it, because the two are
/// different sentences to the reader: a refusal is Windows declining, and this is Deguffer honouring
/// a setting they chose. Reporting a deliberate choice as an obstruction would send them looking for
/// a process that is not there.
/// </param>
/// <param name="Spared">
/// Entries the caller named as off limits and this removal did not descend into (§5.3).
///
/// A third sentence again, for the same reason the second is separate: this is neither Windows
/// refusing nor a setting the user chose, but Deguffer declining to touch something it found
/// somebody using. The user acts on it by closing that program, so the count is what tells them
/// there is something to close.
/// </param>
/// <param name="EntriesRemoved">
/// How many entries this removal took: files, links, and the folders they left empty, the root among
/// them where it went. A path already gone when the removal began took nothing, so it counts none.
/// </param>
public sealed record RemovalOutcome(
    long BytesReclaimed,
    Refusals Refused,
    bool RootRemoved,
    int Kept = 0,
    int Spared = 0,
    long EntriesRemoved = 0)
{
    /// <summary>
    /// The entries directly inside the removal's root that held a refused file, in display form.
    ///
    /// <para>The unit a later preview asks again about, through <see cref="RefusalRecord"/>. The
    /// entry rather than every refused file, because one guarded browser profile is hundreds of
    /// files and a machine can hold two thousand profiles — and the entry rather than the root,
    /// because asking again about a whole tree for one refused file in it would open every other
    /// file in that tree for deletion on every preview.</para>
    /// </summary>
    public IReadOnlyList<string> RefusedAt { get; init; } = [];

    /// <summary>
    /// Every directory this removal tried to take and could not, in display form, whatever kept it:
    /// Windows refusing the folder itself, or something still inside it. A root the caller asked to
    /// keep was never tried, so it is not here. A removal that was <see cref="Interrupted"/> adds the
    /// folders it had gone into and not yet tried.
    ///
    /// <para>The evidence §5.6 reads through <see cref="RunResidue"/>. A folder left standing says
    /// where the removal went, which no question about what a folder still holds can say.</para>
    /// </summary>
    public IReadOnlyList<string> LeftStanding { get; init; } = [];

    /// <summary>
    /// Each spared entry the walk met and held back, in display form.
    ///
    /// <para>The evidence §5.6 reads through <see cref="RunResidue"/> when a spared entry has gone. A
    /// program working in a scratch folder often removes it when it finishes, and without this
    /// nothing could tell that from a removal that failed to spare it.</para>
    ///
    /// <para><b>Only what the walk met, because only that shows no removal took it.</b> The walk met
    /// the entry standing, so nothing in the run had taken it by then, under any spelling. An entry
    /// already gone shows only that this removal did not take it, and an earlier one may have, through
    /// a link no comparison of paths can see. That case is answered by what the run found before it
    /// removed anything: see <see cref="RunResidue.Before"/>. And the walk names an entry only where
    /// it matched one it must leave, so a spelling that failed to match is never recorded.</para>
    /// </summary>
    public IReadOnlyList<string> LeftAlone { get; init; } = [];

    /// <summary>
    /// The Outlook mail stores this removal found and left where they were, in display form, because
    /// Deguffer never removes one (see <see cref="Safety.MailStore"/>). The folders holding them are
    /// in <see cref="LeftStanding"/> for the reason any folder holding something is.
    ///
    /// <para>Paths rather than a count, because what stayed is the evidence a caller checks
    /// afterwards: Explore asserts each is still on the disk, where it has no plan to name them in
    /// advance.</para>
    /// </summary>
    public IReadOnlyList<string> MailStores { get; init; } = [];

    /// <summary>
    /// The folders in <see cref="LeftStanding"/> that Windows refused for a reason of their own, by
    /// reason. See <see cref="FolderRefusals"/> for why a folder held up by what it holds is not
    /// counted. A link left standing is not counted either, for the reason a refused file link is not:
    /// see <see cref="DirectoryRemover"/>.
    /// </summary>
    public FolderRefusals RefusedFolders { get; init; }

    /// <summary>
    /// Whether the removal was cancelled before it finished. Every figure above is still true: it is
    /// what the removal did before it stopped. <see cref="LeftStanding"/> then also holds every folder
    /// it gathered and never tried, because it had gone inside each of them.
    /// </summary>
    public bool Interrupted { get; init; }
}
