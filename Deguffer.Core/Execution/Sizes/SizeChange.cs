using Deguffer.Core.Exploring.History;

namespace Deguffer.Core.Execution.Sizes;

/// <summary>
/// How far a provider's figure moved between two scans (#358), as a number against a date.
///
/// <para>§7.1: growth is a number. Nothing reads one to decide what is ticked or offered, so a row
/// that grew arrives exactly as it would have without the history.</para>
/// </summary>
/// <param name="Bytes">The later figure less the earlier one.</param>
/// <param name="SinceUtc">When the earlier scan ran.</param>
/// <param name="Approximate">
/// Whether either figure was a prediction, or the two scans held different rights, so part of the
/// change may be how the location was read rather than what is in it.
/// </param>
public sealed record SizeChange(long Bytes, DateTime SinceUtc, bool Approximate)
{
    /// <summary>The change from <paramref name="earlier"/> to <paramref name="now"/>, or null where there is no earlier figure.</summary>
    public static SizeChange? Between(SizePoint? earlier, SizePoint now) => earlier is { } then
        ? new SizeChange(
            now.Bytes - then.Bytes,
            then.TakenUtc,
            then.Approximate || now.Approximate || then.Elevated != now.Elevated)
        : null;

    /// <summary>How far it moved, with its sign so growth and shrinkage read apart without colour.</summary>
    public string Amount => Bytes == 0
        ? "No change"
        : Approximate ? $"About {GrowthText.Signed(Bytes)}" : GrowthText.Signed(Bytes);

    /// <summary>How far it moved, and since when.</summary>
    /// <param name="date">Words a UTC instant in the user's own format.</param>
    public string Describe(Func<DateTime, string> date)
    {
        ArgumentNullException.ThrowIfNull(date);

        return $"{Amount} since {date(SinceUtc)}";
    }
}
