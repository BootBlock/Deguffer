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
