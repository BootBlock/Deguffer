using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shading model both rasterisers draw with, checked where it is answered from a table rather
/// than worked out.
///
/// <para><see cref="CushionShading.RidgeAt"/> is asked per pixel of a sunburst and per rectangle per
/// band of a treemap, so it holds its answers for every depth a real tree reaches and falls back to
/// the arithmetic past that. A table and a fallback that disagree would show as a shape suddenly
/// flattening or standing proud at one particular depth, which no other test here would notice.</para>
/// </summary>
public sealed class CushionShadingTests
{
    /// <summary>
    /// Each level's ridge is the same fraction of the level above it, all the way across the point
    /// where the answers stop being held and start being worked out.
    /// </summary>
    [Fact]
    public void TheRidgeFallsByOneConstantRatioAtEveryDepth()
    {
        var ratio = CushionShading.RidgeAt(1) / CushionShading.RidgeAt(0);

        for (var depth = 1; depth < 128; depth++)
        {
            Assert.Equal(
                ratio,
                CushionShading.RidgeAt(depth) / CushionShading.RidgeAt(depth - 1),
                precision: 12);
        }
    }

    /// <summary>
    /// The ridge only ever flattens. An inverted or repeated step would put a deep shape in front of
    /// the one containing it, which is the cue the whole picture reads nesting from.
    /// </summary>
    [Fact]
    public void TheRidgeFlattensWithDepthAndNeverGoesNegative()
    {
        for (var depth = 1; depth < 128; depth++)
        {
            Assert.True(
                CushionShading.RidgeAt(depth) < CushionShading.RidgeAt(depth - 1),
                $"depth {depth} did not flatten");

            Assert.True(CushionShading.RidgeAt(depth) > 0, $"depth {depth} inverted the cushion");
        }
    }
}
