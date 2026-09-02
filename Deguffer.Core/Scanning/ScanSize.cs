namespace Deguffer.Core.Scanning;

/// <summary>
/// What a tree occupies, as the two numbers that can legitimately differ.
///
/// <paramref name="Logical"/> is the sum of file lengths — what Explorer calls "Size", and what
/// re-downloading would cost. <paramref name="Allocated"/> is what the volume gives back on
/// deletion. They diverge on NTFS-compressed and sparse files, and on cluster slack across many
/// small files: a real node_modules tree measured 187,662,336 allocated against 148,155,087
/// logical, and a Recycle Bin whose files are small enough to live inside their own MFT records
/// measured 0 allocated against 903 logical.
///
/// This is *not* §5.4's pair. That one — space freed inside a VHDX versus on the host — cannot be
/// measured from the filesystem at all; it comes from the container tool's own accounting, so it
/// belongs to a provider, not to scanning.
/// </summary>
/// <param name="IsApproximate">
/// True when the number reported is a prediction rather than a measurement:
/// <see cref="HardLinkAwareScanner"/>'s sole-link sum predicts an eviction whose link counts can
/// change under it, and conda's dry run reports what its own clean expects to free. Carried so the
/// UI can say so rather than implying precision the figure does not have.
///
/// <para>The §5.5 fallback walk is deliberately <em>not</em> among them, though it used to be. It
/// reports file lengths, which is exactly the number <see cref="Reclaimable"/> now carries, and it
/// reports them exactly — so hedging it would be a false qualification on every unelevated
/// preview.</para>
/// </param>
public readonly record struct ScanSize(long Allocated, long Logical, bool IsApproximate = false)
{
    public static readonly ScanSize Zero = new(0, 0);

    /// <summary>
    /// A measurement from a source that only knows file lengths — §5.5's fallback walk.
    ///
    /// Allocated is set equal to logical because there is nothing else to put there, and not
    /// because the two are believed equal. Nothing reports allocated bytes to the user, so the
    /// stand-in reaches no screen; it exists so that a walked total and a table total can be added
    /// together without a null.
    /// </summary>
    public static ScanSize FromLengths(long logical) => new(logical, logical);

    /// <summary>
    /// A figure that is a prediction rather than a measurement, and says so. Conda's dry run is the
    /// caller: it reports what its own clean expects to free, which its next run may disagree with.
    /// </summary>
    public static ScanSize Approximate(long logical) => new(logical, logical, IsApproximate: true);

    /// <summary>Approximation is contagious: a total is only as exact as its least exact part.</summary>
    public static ScanSize operator +(ScanSize left, ScanSize right) => new(
        left.Allocated + right.Allocated,
        left.Logical + right.Logical,
        left.IsApproximate || right.IsApproximate);

    /// <summary>
    /// The single number to show and to subtract. It is <see cref="Logical"/>, and that is a
    /// decision taken against measurement rather than the obvious reading of the two fields.
    ///
    /// <para>Allocated is the better answer to "how much space do I get back", and it was this
    /// property for as long as nothing could check it. Three measurements decided against it.</para>
    ///
    /// <para><b>Nothing that deletes can produce it.</b> <see cref="Execution.DirectoryRemover"/>
    /// and <see cref="Execution.FileRemover"/> both count file lengths, so a plan previewing
    /// allocated bytes always reported a logical reclaim afterwards — preview and result on
    /// different axes for every step, not only for the command steps a review caught. Teaching the
    /// walk to read allocated was measured rather than assumed: <c>GetCompressedFileSize</c> costs
    /// 1.1x to 5.4x a length pass and returns the file's length for anything not compressed or
    /// sparse, so it does not produce cluster slack at all; the call that does,
    /// <c>FILE_STANDARD_INFO.AllocationSize</c>, needs a handle per file and took 16.7 seconds over
    /// a 426 MB npm cache against 107 milliseconds for the lengths — 156 times the cost.</para>
    ///
    /// <para><b>Allocated is unavailable more often than it is available.</b> Only the file table
    /// knows it, only an administrator can read the table, and on a real volume the table declined
    /// 13 of 48 measured paths — every one of them because a record in the subtree did not
    /// establish its own size. Half the bytes Deguffer measures on that machine still take the
    /// walk.</para>
    ///
    /// <para><b>Where it was available it was actively wrong to show.</b> A file small enough to
    /// live inside its own MFT record occupies no clusters, so an elevated run measured every
    /// per-volume Recycle Bin at 0 allocated against 903 logical — and a step with nothing to
    /// reclaim cannot be selected. Elevating made seven real locations unselectable.</para>
    ///
    /// <para>Logical is what both routes produce, and they produce the same one: across 322 real
    /// directories the table's logical total and the walk's agreed to the byte, every time. The
    /// honest answer to "what did the volume actually give back" is the free-space delta the shell
    /// already measures across a run and shows beside the total, which no per-tree arithmetic can
    /// improve on.</para>
    /// </summary>
    public long Reclaimable => Logical;
}
