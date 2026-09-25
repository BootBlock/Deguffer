using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>One step of the age ramp: what it means, and what it is drawn in.</summary>
/// <param name="Label">
/// What the legend says beside the swatch. A picture coloured by age with nothing naming the bands
/// is a picture nobody can read, so the words and the colours are one list rather than two that
/// have to be kept in step.
/// </param>
/// <param name="MaximumDays">
/// The band covers everything written within this many days of now. Meaningless on the oldest band
/// and on the unknown one, neither of which has a far edge.
/// </param>
public readonly record struct AgeBand(string Label, int MaximumDays, TileColour Colour);

/// <summary>
/// What colour a shape is painted when the map is coloured by age rather than by branch.
///
/// <para><b>Viridis, and not a hue wheel.</b> Age is an ordered quantity, so the colours have to be
/// ordered too — a reader has to be able to say which of two shapes is older without consulting the
/// legend, and a categorical palette cannot do that however distinguishable its entries are.
/// Viridis is perceptually uniform and monotonic in lightness, so it survives being printed in grey
/// and survives all three common colour-vision deficiencies, which a categorical hue wheel such as
/// <see cref="TilePalette"/>'s does not on hue alone.</para>
///
/// <para><b>Newest is brightest.</b> Recently written work glows and abandoned work recedes, which
/// is the reading a user arrives with. The alternative puts the loudest colour on the thing the
/// page exists to help them stop worrying about.</para>
///
/// <para><b>Bands rather than a continuous ramp.</b> A continuous ramp cannot be given a legend that
/// means anything, and the useful distinctions here are not linear in days: the difference between
/// yesterday and last week matters, and the difference between four years and five does not.</para>
///
/// <para><b>One ramp per <see cref="ExploreScheme"/>.</b> Plasma and magma are viridis's siblings
/// and share its two properties, so a scheme changes how the bands look and not how they read. The
/// soft ramp is viridis washed towards white, which keeps its order. Each is sampled at the points
/// the standard ramp was, so a band sits at the same place along every one of them.</para>
/// </summary>
public static class AgePalette
{
    /// <summary>
    /// Where each dated band ends, newest first. The last has no far edge, so it answers for anything
    /// the ones before it do not claim.
    /// </summary>
    private static readonly (string Label, int MaximumDays)[] Steps =
    [
        ("Today", 1),
        ("This week", 7),
        ("This month", 31),
        ("This year", 365),
        ("1 to 2 years", 730),
        ("2 to 5 years", 1826),
        ("Over 5 years", int.MaxValue),
    ];

    /// <summary>The colour of each step, per <see cref="ExploreScheme"/>, newest first.</summary>
    private static readonly uint[][] Ramps =
    [
        [0xFDE725, 0x7AD151, 0x22A884, 0x2A788E, 0x3B528B, 0x482878, 0x440154],
        [0xF0F921, 0xFCA636, 0xE16462, 0xB12A90, 0x7E03A8, 0x46039F, 0x0D0887],
        [0xFEF078, 0xACE393, 0x76C9B3, 0x7BABB9, 0x8594B7, 0x8D7AAB, 0x8B6295],
        [0xFCFDBF, 0xFEAC76, 0xEF5D5E, 0xB2357B, 0x7C2382, 0x4E117B, 0x251255],
    ];

    /// <summary>
    /// What an entry nothing could date is painted.
    ///
    /// <para>A band rather than an omission. A shape has to be painted something, and painting an
    /// undated one as though it were ancient is the one reading that could get something deleted —
    /// <see cref="Scanning.RelativeAge"/> holds the same rule for the sentence this ends up beside.
    /// The grey is deliberately outside the ramp, so it reads as "not on this scale" rather than as
    /// a step of it.</para>
    /// </summary>
    private static readonly AgeBand Unknown =
        new("Not known", int.MaxValue, TileColour.FromRgb(0x9E9E9E));

    /// <summary>Every scheme's bands, worked out once (G5).</summary>
    private static readonly AgeBand[][] Schemes = [.. Ramps.Select(Banded)];

    /// <summary>
    /// Every band of <paramref name="scheme"/>, newest first, with the unknown one last. What a
    /// legend lists.
    /// </summary>
    public static IReadOnlyList<AgeBand> Bands(ExploreScheme scheme) => Schemes[(int)scheme];

    /// <summary>
    /// The band of <paramref name="scheme"/> an entry last written at <paramref name="when"/> falls in.
    /// </summary>
    /// <param name="nowUtc">
    /// Injected rather than read, so the banding is provable without a clock — the same seam
    /// <see cref="Scanning.RelativeAge.Describe"/> takes for the same reason.
    /// </param>
    public static AgeBand BandOf(ExploreTimestamp when, DateTime nowUtc, ExploreScheme scheme)
    {
        var bands = Schemes[(int)scheme];

        if (when.Utc is not { } written)
        {
            return bands[^1];
        }

        // A file written during the scan, and a clock that disagrees with the filesystem's, both
        // produce a date in the future. Neither is an age, and the newest band is the only reading
        // that is both honest and safe.
        var days = (nowUtc.ToUniversalTime() - written).TotalDays;

        // Searched rather than switched on. The thresholds are already stated once, in the list a
        // legend is drawn from, and stating them a second time here is how a legend comes to
        // disagree with the picture it explains.
        for (var i = 0; i < Steps.Length - 1; i++)
        {
            if (days < bands[i].MaximumDays)
            {
                return bands[i];
            }
        }

        return bands[Steps.Length - 1];
    }

    /// <summary>What to paint a shape last written at <paramref name="when"/>, in <paramref name="scheme"/>.</summary>
    public static TileColour For(ExploreTimestamp when, DateTime nowUtc, ExploreScheme scheme) =>
        BandOf(when, nowUtc, scheme).Colour;

    /// <summary>One ramp's bands, with the unknown one after them.</summary>
    private static AgeBand[] Banded(uint[] ramp) =>
        [.. Steps.Select((step, at) => new AgeBand(step.Label, step.MaximumDays, TileColour.FromRgb(ramp[at]))), Unknown];
}
