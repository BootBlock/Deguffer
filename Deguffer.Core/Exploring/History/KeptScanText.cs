using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// What the Explore page says over the last kept scan of a drive while a new scan of it runs (#491).
///
/// <para>Here rather than in the shell because it states a rule: the map is of the drive as it was,
/// not as it is, and §7.1 lets a picture be wrong only where it says which way.</para>
/// </summary>
public static class KeptScanText
{
    /// <summary>
    /// The line over a map of <paramref name="kept"/>: when that scan was taken, worded as an age column
    /// words it (<see cref="RelativeAge"/>), and that the drive is being scanned again.
    /// </summary>
    public static string Mark(ScanSummary kept, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(kept);

        var age = RelativeAge.Describe(kept.TakenUtc, nowUtc);

        // "Today" and "Yesterday" open a sentence on their own, and here they follow a comma.
        return $"Last scan, {char.ToLowerInvariant(age[0])}{age[1..]}. Scanning again.";
    }

    /// <summary>What a screen reader is told of the map while it shows a kept scan, after <see cref="Mark"/>.</summary>
    public const string Help =
        "The map shows the drive as that scan found it. Nothing on it can be selected until the new scan "
        + "draws the drive as it is.";
}
