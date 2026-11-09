namespace Deguffer.Core.Health;

/// <summary>
/// One verdict from a drive's readings. The readings were judged one by one by the code that knows
/// each protocol, so this holds no protocol knowledge: it takes the worst of them.
/// </summary>
internal static class DriveAssessment
{
    /// <summary>
    /// The worst standing, and Unknown without <paramref name="hasPrimarySignal"/>. Other readings
    /// looking fine say nothing about a drive that did not give its own overall judgement, so they
    /// must not read as a clean bill of health.
    /// </summary>
    internal static DriveVerdict Verdict(IReadOnlyList<HealthAttribute> attributes, bool hasPrimarySignal)
    {
        var worst = attributes.Select(attribute => attribute.Standing).DefaultIfEmpty().Max();

        return worst switch
        {
            AttributeStanding.Failing => DriveVerdict.Failing,
            AttributeStanding.Watch => DriveVerdict.Watch,
            _ => hasPrimarySignal ? DriveVerdict.Good : DriveVerdict.Unknown,
        };
    }

    /// <summary>One sentence per reading of concern, the worst first.</summary>
    internal static IReadOnlyList<string> Findings(IReadOnlyList<HealthAttribute> attributes) =>
    [
        .. attributes
            .Where(attribute => attribute.Standing is not AttributeStanding.Normal)
            .OrderByDescending(attribute => attribute.Standing)
            .Select(attribute => $"{attribute.Name}: {attribute.Reading}. {attribute.Meaning}"),
    ];

    internal static string Summary(DriveVerdict verdict) => verdict switch
    {
        DriveVerdict.Failing => "The drive reports a serious problem. Copy your important files somewhere else now.",
        DriveVerdict.Watch => "At least one reading needs attention. Back up important files.",
        DriveVerdict.Good => "No problems reported.",
        _ => "Windows could not read this drive's overall health.",
    };
}
