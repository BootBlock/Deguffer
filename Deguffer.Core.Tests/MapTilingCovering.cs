using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>What <see cref="MapTiling.Covering"/> writes, as a list of its own, for a test that asks once.</summary>
internal static class MapTilingCovering
{
    public static List<MapTileKey> Covered(this MapTiling tiling, MapViewport shown, int level, double focusX, double focusY)
    {
        var keys = new List<MapTileKey>();

        tiling.Covering(shown, level, focusX, focusY, keys);

        return keys;
    }
}
