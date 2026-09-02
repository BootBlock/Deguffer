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
    ValueTask<ScanResult> MeasureAsync(
        string path,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Measure <paramref name="path"/> without consulting anything remembered from before now.
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
    /// <see cref="MeasureAsync"/>.</para>
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
    /// </summary>
    ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Every directory named <paramref name="name"/> at or below <paramref name="root"/>, or null
    /// when this scanner cannot answer without walking.
    ///
    /// Null is the observable fallback, in the same spirit as <see cref="FallbackReason"/>: the
    /// caller walks <paramref name="root"/> itself and says so, rather than being handed an empty
    /// list it cannot distinguish from "there are none". Only the volume index can answer this, and
    /// it exists only when Deguffer is elevated, so the walk is the guaranteed route and this is
    /// strictly an accelerator.
    ///
    /// <paramref name="root"/> is a boundary, not a hint. Directories found elsewhere on the volume
    /// are not returned — the index makes discovery cheap, and that must not make consent implicit.
    /// </summary>
    ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default);

    /// <summary>
    /// Drop cached volume indexes and sizes. Called before a planning pass, for the same reason
    /// providers drop theirs: a preview must describe the machine as it is now.
    /// </summary>
    void Invalidate();
}
