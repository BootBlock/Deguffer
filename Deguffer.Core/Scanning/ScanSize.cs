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
/// <param name="Entries">
/// How many filesystem entries a removal of the measured path would take: its files, its
/// directories and the path itself, and the links inside it, which are removed as links.
///
/// <para><b>Bytes are not the whole of what a removal frees.</b> A folder holding nothing measures
/// zero bytes and is still an entry in its parent, and a leftover made of hundreds of empty folders
/// is hundreds of entries nothing will ever read. Without a count, that leftover could not be told
/// from a path holding nothing to remove at all.</para>
///
/// <para><b>Counted on the removal's own terms, and never above them.</b> A file the guard on
/// recently changed files keeps is not counted, and neither is any folder above it, because a
/// folder still holding something cannot be removed. The same holds for a folder the walk was
/// refused, whose contents nobody read. Two things fall short of the removal instead, which is the
/// safe direction for a count that decides whether anything is offered: a symbolic link to a file,
/// which neither measuring route sees, and a sole-link prediction, which counts nothing because the
/// tool it forecasts decides what it removes.</para>
/// </param>
/// <param name="IsCeiling">
/// True when the number is the most the removal could free, and it is known beforehand to free less:
/// the component store's overhead, which counts the payload of switched-off features that no cleanup
/// removes. "About" would state it as a forecast, which it is not.
/// </param>
public readonly record struct ScanSize(
    long Allocated, long Logical, bool IsApproximate = false, long Entries = 0, bool IsCeiling = false)
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

    /// <summary>
    /// The most a removal could free, which it is known to fall short of, and says so. See
    /// <see cref="IsCeiling"/>.
    /// </summary>
    public static ScanSize Ceiling(long logical) => new(logical, logical, IsCeiling: true);

    /// <summary>
    /// Approximation is contagious: a total is only as exact as its least exact part. So is a
    /// ceiling: a total with a part that frees less than its figure frees less than its own.
    /// </summary>
    public static ScanSize operator +(ScanSize left, ScanSize right) => new(
        left.Allocated + right.Allocated,
        left.Logical + right.Logical,
        left.IsApproximate || right.IsApproximate,
        left.Entries + right.Entries,
        left.IsCeiling || right.IsCeiling);

    /// <summary>
    /// A measurement with part of it taken out: what a folder holds, less the part of it a plan has
    /// undertaken to leave alone.
    ///
    /// <para><b>Clamped at zero, which the addition above needs no equivalent of.</b> The two
    /// figures are separate measurements taken moments apart, so a folder that shrank in between
    /// can make the subtrahend the larger — and a negative estimate would be subtracted from a
    /// plan's total and report that cleaning gains space elsewhere. Nothing is the honest answer:
    /// there is no longer anything to reclaim.</para>
    ///
    /// <para>Approximation is contagious in the same direction as it is for a sum. A figure with an
    /// inexact part removed from it is not thereby exact.</para>
    /// </summary>
    public static ScanSize operator -(ScanSize left, ScanSize right) => new(
        Math.Max(0, left.Allocated - right.Allocated),
        Math.Max(0, left.Logical - right.Logical),
        left.IsApproximate || right.IsApproximate,
        Math.Max(0, left.Entries - right.Entries),
        left.IsCeiling || right.IsCeiling);

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
    /// different axes for every step, not only for the command steps. Teaching the
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
