namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The regions a canvas is painted in, in the order they are put on screen.
///
/// <para>A redraw puts each region up as soon as it is painted rather than the whole canvas at
/// once, so the picture fills in instead of appearing after a pause. The order is where the reader
/// is looking: the region under the pointer first, then outwards from the middle of the canvas,
/// which is where a treemap puts its largest shapes and where a sunburst puts its root.</para>
/// </summary>
public static class PaintOrder
{
    /// <summary>
    /// How wide and how tall a region is, in device pixels.
    ///
    /// <para>Small enough that a 4K canvas is over a hundred regions, so the first of them is on
    /// screen a small fraction of the way through the paint, and so the threads painting them have
    /// spare regions to share out where one region holds far more shapes than another. Large enough
    /// that putting each one on screen, which is a copy of its rows and one write to the GPU, is not
    /// dominated by the cost of starting them. A region is never larger than this, so one staging
    /// buffer of this size serves every write.</para>
    /// </summary>
    public const int RegionSize = 256;

    /// <summary>
    /// Cut a canvas of <paramref name="width"/> by <paramref name="height"/> into regions, the one
    /// holding <paramref name="focus"/> first and the rest by how far their middles are from the
    /// canvas's middle. The regions tile the canvas exactly, with no gap and no overlap.
    /// </summary>
    /// <param name="focus">Where the pointer is on the canvas, or null where it is not over it.</param>
    public static IReadOnlyList<CanvasRegion> Regions(int width, int height, ExplorePoint? focus)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var columns = (width + RegionSize - 1) / RegionSize;
        var rows = (height + RegionSize - 1) / RegionSize;
        var regions = new CanvasRegion[columns * rows];
        var distances = new float[regions.Length];

        var middleX = width / 2f;
        var middleY = height / 2f;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = column * RegionSize;
                var y = row * RegionSize;
                var region = new CanvasRegion(x, y, Math.Min(RegionSize, width - x), Math.Min(RegionSize, height - y));
                var index = (row * columns) + column;

                var dx = x + (region.Width / 2f) - middleX;
                var dy = y + (region.Height / 2f) - middleY;

                regions[index] = region;
                distances[index] = focus is { } point && region.Contains(point.X, point.Y)
                    ? -1
                    : (dx * dx) + (dy * dy);
            }
        }

        Array.Sort(distances, regions);

        return regions;
    }
}
