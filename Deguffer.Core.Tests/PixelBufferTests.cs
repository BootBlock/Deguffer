using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The BGRA canvas a drawing is painted into, and the copy of one region of it that goes up to the
/// screen on its own.
/// </summary>
public sealed class PixelBufferTests
{
    /// <summary>
    /// A region is uploaded by itself, packed with no gap between its rows. A copy that took the wrong
    /// stride, or started a row or a column out, would put another part of the picture on screen in
    /// that region's place, so the shapes there would not be the ones a click finds.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 3, 2)]
    [InlineData(2, 1, 4, 3)]
    [InlineData(6, 4, 1, 1)]
    public void ARegionIsCopiedRowByRowWithNothingElse(int x, int y, int regionWidth, int regionHeight)
    {
        const int width = 7;
        const int height = 5;
        var pixels = new byte[PixelBuffer.LengthFor(width, height)];

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);
        }

        var region = new CanvasRegion(x, y, regionWidth, regionHeight);
        var copied = new byte[(regionWidth * regionHeight * 4) + 4];

        PixelBuffer.CopyRegion(pixels, width, region, copied);

        for (var row = 0; row < regionHeight; row++)
        {
            for (var column = 0; column < regionWidth * 4; column++)
            {
                var expected = pixels[((((y + row) * width) + x) * 4) + column];

                Assert.Equal(expected, copied[(row * regionWidth * 4) + column]);
            }
        }

        // Nothing past the region's own bytes is written.
        Assert.All(copied[^4..], value => Assert.Equal(0, value));
    }
}
