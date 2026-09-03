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
/// and survives all three common colour-vision deficiencies. That last property is the one
/// <see cref="TilePalette"/> chose Okabe and Ito for, arrived at from the opposite direction.</para>
///
/// <para><b>Newest is brightest.</b> Recently written work glows and abandoned work recedes, which
/// is the reading a user arrives with. The alternative puts the loudest colour on the thing the
/// page exists to help them stop worrying about.</para>
///
/// <para><b>Bands rather than a continuous ramp.</b> A continuous ramp cannot be given a legend that
/// means anything, and the useful distinctions here are not linear in days: the difference between
/// yesterday and last week matters, and the difference between four years and five does not.</para>
/// </summary>
public static class AgePalette
{
    /// <summary>
    /// The dated bands, newest first. The last has no far edge, so it answers for anything the ones
    /// before it do not claim.
    /// </summary>
    private static readonly AgeBand[] Dated =
    [
        new("Today", 1, TileColour.FromRgb(0xFDE725)),
        new("This week", 7, TileColour.FromRgb(0x7AD151)),
        new("This month", 31, TileColour.FromRgb(0x22A884)),
        new("This year", 365, TileColour.FromRgb(0x2A788E)),
        new("1 to 2 years", 730, TileColour.FromRgb(0x3B528B)),
        new("2 to 5 years", 1826, TileColour.FromRgb(0x482878)),
        new("Over 5 years", int.MaxValue, TileColour.FromRgb(0x440154)),
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

    /// <summary>Every band, newest first, with the unknown one last. What a legend lists.</summary>
    public static IReadOnlyList<AgeBand> Bands { get; } = [.. Dated, Unknown];

    /// <summary>The band an entry last written at <paramref name="when"/> falls in.</summary>
    /// <param name="nowUtc">
    /// Injected rather than read, so the banding is provable without a clock — the same seam
    /// <see cref="Scanning.RelativeAge.Describe"/> takes for the same reason.
    /// </param>
    public static AgeBand BandOf(ExploreTimestamp when, DateTime nowUtc)
    {
        if (when.Utc is not { } written)
        {
            return Unknown;
        }

        // A file written during the scan, and a clock that disagrees with the filesystem's, both
        // produce a date in the future. Neither is an age, and the newest band is the only reading
        // that is both honest and safe.
        var days = (nowUtc.ToUniversalTime() - written).TotalDays;

        // Searched rather than switched on. The thresholds are already stated once, in the list a
        // legend is drawn from, and stating them a second time here is how a legend comes to
        // disagree with the picture it explains.
        for (var i = 0; i < Dated.Length - 1; i++)
        {
            if (days < Dated[i].MaximumDays)
            {
                return Dated[i];
            }
        }

        return Dated[^1];
    }

    /// <summary>What to paint a shape last written at <paramref name="when"/>.</summary>
    public static TileColour For(ExploreTimestamp when, DateTime nowUtc) => BandOf(when, nowUtc).Colour;
}
