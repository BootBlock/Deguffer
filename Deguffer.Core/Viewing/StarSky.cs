namespace Deguffer.Core.Viewing;

/// <summary>One star of the About page's starfield.</summary>
/// <param name="X">Across, from the line of flight, in device-independent pixels.</param>
/// <param name="Y">Down, from the line of flight.</param>
/// <param name="Sheet">Which of the field's sheets it is on, each at its own depth.</param>
public readonly record struct Star(float X, float Y, int Sheet);

/// <summary>
/// Where the stars of the About page's starfield are: scattered evenly over a disc across the line of
/// flight, and dealt evenly across sheets that stand one behind another through the depth.
///
/// <para>A sheet is flown through as one, so the compositor moves a few dozen sheets rather than
/// thousands of stars. The disc has a hole in its middle, because a star on the line of flight would
/// come straight at the camera and swell to fill the screen rather than stream past it.</para>
/// </summary>
public static class StarSky
{
    /// <summary>
    /// <paramref name="count"/> stars over <paramref name="sheets"/> sheets, between
    /// <paramref name="hole"/> and <paramref name="radius"/> from the line of flight. The same
    /// <paramref name="seed"/> scatters the same sky.
    /// </summary>
    public static IReadOnlyList<Star> Scatter(int count, int sheets, float hole, float radius, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sheets);
        ArgumentOutOfRangeException.ThrowIfNegative(hole);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(radius, hole);

        var random = new Random(seed);
        var stars = new Star[count];

        for (var i = 0; i < count; i++)
        {
            // Even by area rather than by distance, or the stars would crowd the middle of the disc:
            // a ring twice as far out is twice as long.
            var distance = MathF.Sqrt((random.NextSingle() * ((radius * radius) - (hole * hole))) + (hole * hole));
            var angle = random.NextSingle() * 2 * MathF.PI;

            stars[i] = new Star(distance * MathF.Cos(angle), distance * MathF.Sin(angle), i % sheets);
        }

        return stars;
    }
}
