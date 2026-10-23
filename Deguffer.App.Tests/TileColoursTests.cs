using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Rendering;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>A map's colour reaches the screen opaque and channel for channel, and a theme's colour comes back the same way.</summary>
public sealed class TileColoursTests
{
    [Fact]
    public void AMapColourIsDrawnOpaqueWithItsOwnChannels() =>
        Assert.Equal(Color.FromArgb(255, 200, 80, 40), new TileColour(200, 80, 40).ToColor());

    /// <summary>The alpha goes: a translucent theme colour would otherwise have no map colour to be.</summary>
    [Fact]
    public void AWindowsColourKeepsItsChannelsAndDropsItsAlpha() =>
        Assert.Equal(new TileColour(10, 20, 30), Color.FromArgb(128, 10, 20, 30).ToTile());
}
