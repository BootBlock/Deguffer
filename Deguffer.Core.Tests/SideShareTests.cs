using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>A card beside a picture takes a share of the width, within the widths its content reads at.</summary>
public sealed class SideShareTests
{
    [Fact]
    public void AMiddlingRegionGivesTheCardItsShare() =>
        Assert.Equal(300, SideShare.Width(750, 0.4, 240, 340));

    /// <summary>On a narrow window the card stops shrinking where its lists would stop reading.</summary>
    [Fact]
    public void ANarrowRegionHoldsTheCardAtItsNarrowest() =>
        Assert.Equal(240, SideShare.Width(500, 0.4, 240, 340));

    /// <summary>On a wide window the picture takes the rest, rather than the card growing past what it holds.</summary>
    [Fact]
    public void AWideRegionHoldsTheCardAtItsWidest() =>
        Assert.Equal(340, SideShare.Width(1600, 0.4, 240, 340));

    [Fact]
    public void ACardThatLeavesThePictureItsRoomSitsBesideIt() =>
        Assert.False(SideShare.Stacks(available: 700, side: 240, gap: 8, mainMinimum: 400));

    /// <summary>A narrow window would leave the list beside the card too narrow for a name and its figures, so the card goes under it.</summary>
    [Fact]
    public void ACardThatWouldSqueezeThePictureGoesUnderIt() =>
        Assert.True(SideShare.Stacks(available: 536, side: 240, gap: 8, mainMinimum: 400));

    /// <summary>A picture left exactly its narrowest still has room for a name and its figures.</summary>
    [Fact]
    public void APictureLeftExactlyItsNarrowestKeepsTheCardBesideIt() =>
        Assert.False(SideShare.Stacks(available: 648, side: 240, gap: 8, mainMinimum: 400));

    /// <summary>The gap between the two is not the picture's to draw in, so it counts against it.</summary>
    [Fact]
    public void TheGapCountsAgainstThePicture() =>
        Assert.True(SideShare.Stacks(available: 644, side: 240, gap: 8, mainMinimum: 400));

    [Fact]
    public void AnUnboundedRegionGivesTheCardItsWidest() =>
        Assert.Equal(340, SideShare.Width(double.PositiveInfinity, 0.4, 240, 340));
}
