using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// A copy to remove, held and found to hold what the copy its group keeps holds (§7.4): both held
/// copies, and the copy's named streams as they were compared. It closes the copy's handle and not
/// the kept copy's, which its group holds for every copy it compares.
/// </summary>
internal sealed class ComparedCopy(HeldCopy held, HeldCopy kept, IReadOnlyList<NamedStream> streams) : IDisposable
{
    public DuplicateCandidate Copy => held.Copy;

    public HeldCopy Held => held;

    public HeldCopy Kept => kept;

    /// <summary>
    /// Why the comparison no longer shows the two are the same, or null where it still does. Asked
    /// immediately before the copy goes: sharing refuses a write, not a change to times or
    /// attributes, nor a stream added beside the content, so both copies are described again.
    /// </summary>
    public CopyRemoval? Changed(FileInformation files) =>
        (kept.Recheck(files) ?? held.Recheck(files)) is not null
        || CopyComparison.StreamsOf(held.Content) is not { } after
        || !CopyComparison.SameStreams(streams, after)
            ? new CopyRemoval(Copy, RemovalCheck.Changed,
                "It or the copy kept changed after they were compared, so the comparison no longer shows they are the same.")
            : null;

    public void Dispose() => held.Dispose();
}
