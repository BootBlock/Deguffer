using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>Where the About page's stars are scattered.</summary>
public sealed class StarSkyTests
{
    private const float Hole = 60;
    private const float Radius = 1600;

    /// <summary>
    /// No star sits on or near the line of flight, where it would come straight at the camera and swell
    /// to fill the screen, and none is beyond the disc.
    /// </summary>
    [Fact]
    public void EveryStarIsBetweenTheHoleAndTheEdge()
    {
        var stars = StarSky.Scatter(20_000, sheets: 80, Hole, Radius, seed: 7);

        Assert.All(stars, star =>
            Assert.InRange(MathF.Sqrt((star.X * star.X) + (star.Y * star.Y)), Hole - 0.01f, Radius + 0.01f));

        // The fixture has to put stars near the hole for the bound to be tested at all.
        Assert.Contains(stars, star => MathF.Sqrt((star.X * star.X) + (star.Y * star.Y)) < Hole * 1.5f);
    }

    /// <summary>
    /// The stars are even by area, so the outer half of the disc, three quarters of its area, has about
    /// three quarters of them, not the half an even spread by distance would give it.
    /// </summary>
    [Fact]
    public void TheStarsAreEvenOverTheDisc()
    {
        var stars = StarSky.Scatter(20_000, sheets: 80, hole: 0, Radius, seed: 7);
        var outer = stars.Count(star => MathF.Sqrt((star.X * star.X) + (star.Y * star.Y)) > Radius / 2);

        Assert.InRange(outer / (double)stars.Count, 0.72, 0.78);
    }

    /// <summary>Every sheet carries its share, so no depth of the field is bare.</summary>
    [Fact]
    public void EverySheetHasItsShare()
    {
        var stars = StarSky.Scatter(2_000, sheets: 80, Hole, Radius, seed: 7);

        Assert.All(stars.GroupBy(star => star.Sheet), sheet => Assert.Equal(25, sheet.Count()));
        Assert.Equal(80, stars.Select(star => star.Sheet).Distinct().Count());
    }

    [Fact]
    public void TheSameSeedScattersTheSameSky() =>
        Assert.Equal(
            StarSky.Scatter(500, sheets: 20, Hole, Radius, seed: 3),
            StarSky.Scatter(500, sheets: 20, Hole, Radius, seed: 3));
}
