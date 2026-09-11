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
public sealed record RemovalOutcome(
    long BytesReclaimed,
    Refusals Refused,
    bool RootRemoved,
    int Kept = 0,
    int Spared = 0)
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
}
