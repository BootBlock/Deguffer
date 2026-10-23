using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The one way anything in Deguffer learns how big a directory is.
///
/// This seam exists so that §5.5's two routes — the MFT and the bounded parallel walk — stay a
/// scanning concern. Providers, the planner and the executor ask for a size; none of them knows
/// there is a choice to make, and none of them should, because the choice depends on the volume
/// and the process token rather than on anything about a cache.
/// </summary>
public interface IDirectoryScanner
{
    /// <summary>
    /// Measure <paramref name="path"/>, reporting running subtotals as they accumulate.
    ///
    /// §5.5: never block on a complete scan. <paramref name="progress"/> receives partial totals so
    /// the preview can populate as the number grows; the returned result is the final figure.
    /// A path that does not exist measures zero rather than throwing — an absent cache is a normal
    /// answer, not an error.
    /// </summary>
    /// <param name="keep">
    /// Files the user has asked to be left alone because they were touched recently, which are then
    /// not part of the total either. The figure and the deletion have to agree about what is coming
    /// out, or the preview promises bytes the clean will not take — §5.4's "prune, see no change,
    /// lose trust" arriving from the opposite direction.
    ///
    /// <para><see cref="MinimumAge.Off"/> by default, which is the shipped preference, so a caller
    /// that says nothing measures the whole tree exactly as before.</para>
    /// </param>
    /// <param name="bounds">
    /// What the removal of <paramref name="path"/> leaves standing beyond what <paramref name="keep"/>
    /// keeps: the root, and entries it neither takes nor enters. Every route honours it, so a caller
    /// never measures an entry only to subtract it. The whole tree by default.
    /// </param>
    ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Measure each of <paramref name="asked"/> as <see cref="MeasureAsync"/> measures it, all in one
    /// go, and answer in the order asked.
    ///
    /// <para>One go rather than one call a path, because a walk of one small tree leaves most of its
    /// threads idle, and a provider with hundreds of targets paid that once a target. A walking scanner
    /// seeds one walk with every directory, and keeps a figure for each. A path inside another asked
    /// about here is read off the outer path's walk where that walk meets it, so it is not walked twice.
    /// The volume index answers each path in a lookup of its own.</para>
    ///
    /// <para>Each answer is the whole of what <see cref="MeasureAsync"/> would have said of its path:
    /// its own <see cref="ScanResult.Root"/>, mail stores, fallback reason and
    /// <see cref="ScanResult.WithheldRecent"/>. No progress is reported, because no caller of a list has
    /// one figure to show.</para>
    /// </summary>
    /// <param name="keep">The guard every path is measured under, as <see cref="MeasureAsync"/> applies it.</param>
    ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
        IReadOnlyList<ScanRequest> asked,
        MinimumAge keep = default,
        CancellationToken ct = default);

    /// <summary>
    /// Measure <paramref name="path"/>'s whole tree, and the part of it older than
    /// <paramref name="age"/>, from one reading of the tree.
    ///
    /// <para>For a command that takes an age itself, such as File History's: the estimate has to be
    /// the aged part, which is all the command will consider, and the figure the executor subtracts
    /// its after-measure from has to be the whole, because that after-measure is unguarded. Asking
    /// <see cref="MeasureAsync"/> twice walks the tree twice, and the tree is usually a backup disk
    /// or a network share, where a second walk costs as much as the first. Every listing already
    /// carries each file's timestamps, so one walk answers both.</para>
    ///
    /// <para>Each reading is exactly what <see cref="MeasureAsync"/> would return for it, entry
    /// count included, because the folders an age keeps standing are counted apart from the ones the
    /// whole tree's removal takes. A remembered figure is never shown, because nothing reports
    /// progress here.</para>
    /// </summary>
    ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default);

    /// <summary>
    /// Measure each of <paramref name="asked"/> without consulting anything remembered from before now,
    /// and answer in the order asked.
    ///
    /// <para><see cref="MeasureAsync"/> is free to answer from a volume snapshot, and that is the
    /// whole of §5.5's speed. A snapshot is only ever fresh relative to when it was taken, though,
    /// and one caller subtracts two readings across an event that changed the disk between them:
    /// the executor, reporting what a tool's own eviction command freed. Served from one snapshot
    /// both readings are identical, they cancel, and a clean that freed gigabytes reports nothing —
    /// §5.4's stated failure, "the user will prune, see no change, and lose trust in the tool",
    /// arriving by a different route.</para>
    ///
    /// <para>A separate member rather than a flag on <see cref="MeasureAsync"/>, because the rule is
    /// not a tuning option: a figure subtracted from an earlier one has to come from the disk, and
    /// that is a property of the question rather than of the caller's patience. The two scanners
    /// that hold nothing between calls answer this exactly as they answer
    /// <see cref="MeasureManyAsync"/> with the guard off.</para>
    ///
    /// <para><b>The cost is a walk, and it is not always the cheap one.</b> After a successful
    /// eviction the tree is nearly empty and the walk is nearly free, which is the ordinary case for
    /// npm, pip, uv, Go and NuGet. It is not the case for conda, whose clean deliberately leaves
    /// every package an environment still links, nor for a command that failed — and §5.5 measured
    /// that walk at over ten minutes across a handful of profile subtrees. Nothing is reported while
    /// it runs, because a command step has no progress to report against in the first place.</para>
    ///
    /// <para>The alternative was rebuilding the volume index between the command and the measure,
    /// which drops every volume and costs seconds apiece, repeated once per command step in a run.
    /// Neither option is free; this one is wrong in the direction of being slow rather than in the
    /// direction of reporting a number nobody can check.</para>
    ///
    /// <para>A list rather than one path, because a command step reads several paths, and the walk
    /// takes them together as <see cref="MeasureManyAsync"/> takes them, a path inside another included.
    /// Each answer is the one this reading would give its path alone.</para>
    /// </summary>
    /// <param name="asked">
    /// Each path, with what to leave standing on the terms the reading it is subtracted from was taken
    /// on: an emptied Recycle Bin's folder is measured with its root kept, as its estimate was. See
    /// <see cref="ScanBounds"/>.
    /// </param>
    ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default);

    /// <summary>
    /// Every directory <paramref name="search"/> finds below <paramref name="root"/>, as full paths
    /// in ordinal order, or null when this scanner cannot answer without walking.
    ///
    /// Null is the observable fallback, in the same spirit as <see cref="FallbackReason"/>: the
    /// caller walks <paramref name="root"/> itself and says so, rather than being handed an empty
    /// list it cannot distinguish from "there are none". Only the volume index can answer this, and
    /// it exists only when Deguffer is elevated, so the walk is the guaranteed route and this is
    /// strictly an accelerator. A root the index holds no subtree for, such as one reached through a
    /// link, is answered with null for the same reason.
    ///
    /// <paramref name="root"/> is a boundary, not a hint. Directories found elsewhere on the volume
    /// are not returned — the index makes discovery cheap, and that must not make consent implicit.
    /// The answer is the search's whole answer: it stops where <paramref name="search"/> stops, so a
    /// caller has nothing left to filter.
    /// </summary>
    ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default);

    /// <summary>
    /// Whether a caller that can walk <c>root</c> itself should walk while
    /// <see cref="TryFindDirectoriesAsync"/> waits for the volume index, and take whichever
    /// answers first. False for a scanner that answers at once, and for one the user has asked to
    /// wait for the table. See <see cref="Configuration.ScanRoute.Auto"/>.
    /// </summary>
    bool RacesTheWalk => false;

    /// <summary>
    /// Drop cached volume indexes and sizes. Called before a planning pass, for the same reason
    /// providers drop theirs: a preview must describe the machine as it is now.
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Keep what this scanner builds to answer a planning pass, its volume indexes, until the hold
    /// ends, and give it back once no hold is left. Taken by the planner for the length of each
    /// pass, so an index that nothing will read again is not kept while the app sits idle.
    /// </summary>
    PassHold Hold();
}
