using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>An ATA drive's attributes as rows, each stating what it means and whether it is a concern.</summary>
internal static class AtaAttributeRows
{
    /// <summary>The drive's own overall prediction, which is what a Good verdict rests on.</summary>
    internal const string PredictionKey = "prediction";

    private const string SelfTestKey = "self-test";
    private const string ErrorCountKey = "errors";
    private const string WriteAmplificationKey = "write-amplification";

    private const int LifeLeftWarningPercent = 10;

    /// <summary>The range of values and thresholds that mean something. 0, 254 and 255 are reserved.</summary>
    private const int LowestValid = 1;

    private const int HighestValid = 253;

    private const string UnusedNumbers = "Manufacturers use this number differently, so Deguffer shows it as reported.";
    private const string UnstatedGigabyte = "The drive counts in gigabytes, and its maker does not say whether a gigabyte is a thousand or 1,024 megabytes, so Deguffer shows the count as the drive gives it.";
    private const string BackUpAdvice = "This can mean the drive is starting to fail. Back up important files.";

    internal static HealthAttribute Prediction(bool predictsFailure) => new(
        PredictionKey,
        "Drive's own failure prediction",
        predictsFailure ? "Failure predicted" : "No failure predicted",
        predictsFailure
            ? "The drive predicts that it will fail. Copy your important files somewhere else now."
            : "The drive's own check does not predict a failure.",
        predictsFailure ? AttributeStanding.Failing : AttributeStanding.Normal,
        predictsFailure ? "predicted" : "not predicted");

    /// <param name="logicalSectorBytes">The drive's logical sector size, or null where Windows did not report it.</param>
    internal static IEnumerable<HealthAttribute> Of(IReadOnlyList<AtaAttribute> attributes, DeviceDescriptor drive, uint? logicalSectorBytes)
    {
        var rows = attributes.Select(attribute => Row(attribute, drive, logicalSectorBytes));

        return WriteAmplification(attributes, drive) is { } amplification ? rows.Append(amplification) : rows;
    }

    internal static HealthAttribute SelfTest(SelfTestOutcome outcome) => outcome.Result switch
    {
        SelfTestResult.Passed => SelfTestRow("Passed", "The drive's last self-test found no fault.", outcome, AttributeStanding.Normal),
        SelfTestResult.Stopped => SelfTestRow("Stopped early", "The drive's last self-test did not finish, so it says nothing either way.", outcome, AttributeStanding.Normal),
        SelfTestResult.Running => SelfTestRow("Running", "The drive is running a self-test now.", outcome, AttributeStanding.Normal),
        _ => SelfTestRow("Failed", "The drive's last self-test found a fault. Copy your important files somewhere else now.", outcome, AttributeStanding.Failing),
    };

    internal static HealthAttribute ErrorCount(int count) => new(
        ErrorCountKey,
        "Errors in the drive's log",
        HealthText.Count((UInt128)count),
        count == 0
            ? "The drive has not logged any errors."
            : "Errors the drive has logged over its life. A few old ones are common, so the log alone is not a sign of failure.",
        AttributeStanding.Normal,
        $"{count}");

    private static HealthAttribute SelfTestRow(string reading, string meaning, SelfTestOutcome outcome, AttributeStanding standing) =>
        new(SelfTestKey, "Last self-test", reading, meaning, standing, $"at {outcome.PowerOnHours} hours of use");

    /// <summary>
    /// How many pages the drive writes to flash for each page the computer writes, where a document
    /// defines both counts. Nothing is stated before the computer has written anything.
    /// </summary>
    private static HealthAttribute? WriteAmplification(IReadOnlyList<AtaAttribute> attributes, DeviceDescriptor drive)
    {
        if (AtaVendorOverlay.FlashPagesWritten(drive) is not (var hostId, var driveId)
            || attributes.FirstOrDefault(attribute => attribute.Id == hostId) is not { Id: not 0, Raw: > 0 and var host }
            || attributes.FirstOrDefault(attribute => attribute.Id == driveId) is not { Id: not 0, Raw: var own })
        {
            return null;
        }

        var factor = ((double)host + own) / host;

        return new HealthAttribute(
            WriteAmplificationKey,
            "Write amplification",
            factor.ToString("0.00", null),
            "How many pages the drive writes to flash memory for each page the computer writes. The drive's own upkeep adds the extra, and a lower figure wears the drive more slowly.",
            AttributeStanding.Normal,
            $"IDs {hostId} and {driveId}: raw {host} and {own}");
    }

    private static HealthAttribute Row(AtaAttribute attribute, DeviceDescriptor drive, uint? logicalSectorBytes)
    {
        var known = AtaVendorOverlay.Of(attribute.Id, drive) ?? AtaAttributeCatalogue.Of(attribute.Id);
        var raw = RawText(attribute);

        if (known is null)
        {
            return Judged(attribute, drive, $"Vendor-specific attribute {attribute.Id}", Value(attribute), UnusedNumbers, AttributeStanding.Normal, raw);
        }

        var (reading, meaning, standing) = Reading(attribute, known, logicalSectorBytes);
        return Judged(attribute, drive, known.Name, reading, meaning, standing, raw);
    }

    /// <summary>A reading at or below the drive's own threshold is the drive's own verdict, so it stands whatever the raw value's format.</summary>
    private static HealthAttribute Judged(AtaAttribute attribute, DeviceDescriptor drive, string name, string reading, string meaning, AttributeStanding standing, string raw) =>
        IsPastThreshold(attribute, drive)
            ? new HealthAttribute(
                Key(attribute),
                name,
                reading,
                "The drive rates this reading as failed by its own limit. Copy your important files somewhere else now.",
                AttributeStanding.Failing,
                raw)
            : new HealthAttribute(Key(attribute), name, reading, meaning, standing, raw);

    private static bool IsPastThreshold(AtaAttribute attribute, DeviceDescriptor drive) =>
        attribute is { IsPreFail: true, Threshold: { } threshold, Value: var value }
        && (AtaVendorOverlay.AlwaysFails(threshold, drive)
            || (threshold is >= LowestValid and <= HighestValid
                && value is >= LowestValid and <= HighestValid
                && value <= threshold));

    private static (string Reading, string Meaning, AttributeStanding Standing) Reading(AtaAttribute attribute, AtaAttributeDefinition known, uint? logicalSectorBytes) => known switch
    {
        AtaStatusDefinition status => Status(attribute, status),
        { Format: SmartRawFormat.Opaque } => (Value(attribute), $"{known.Explanation} {UnusedNumbers}".TrimStart(), AttributeStanding.Normal),
        { Role: AttributeRole.FailureCount } => FailureCount(attribute, known),
        { Role: AttributeRole.RemainingLife } => RemainingLife(attribute, known),
        { Role: AttributeRole.Temperature } => (AtaRawReading.Temperature(attribute, known.Format), known.Explanation, AttributeStanding.Normal),
        { Format: SmartRawFormat.Gigabytes } => (HealthText.StatedGigabytes(AtaRawReading.Figure(attribute, known)), $"{known.Explanation} {UnstatedGigabyte}", AttributeStanding.Normal),
        _ => (AtaRawReading.Decoded(attribute, known, logicalSectorBytes), known.Explanation, AttributeStanding.Normal),
    };

    private static (string, string, AttributeStanding) Status(AtaAttribute attribute, AtaStatusDefinition status) =>
        status.Codes.TryGetValue(attribute.Raw, out var code)
            ? (code.Reading, $"{status.Explanation} {code.Meaning}", code.Standing)
            : (AtaRawReading.Code(attribute), $"{status.Explanation} The manufacturer's document does not list this code, so Deguffer shows it as reported.", AttributeStanding.Normal);

    private static (string, string, AttributeStanding) FailureCount(AtaAttribute attribute, AtaAttributeDefinition known)
    {
        var count = AtaRawReading.Figure(attribute, known);

        return count == 0
            ? ("None", $"{known.Explanation} None so far.", AttributeStanding.Normal)
            : (HealthText.Count(count), $"{known.Explanation} {BackUpAdvice}", AttributeStanding.Watch);
    }

    private static (string, string, AttributeStanding) RemainingLife(AtaAttribute attribute, AtaAttributeDefinition known)
    {
        var left = AtaRawReading.Percentage(attribute, known);

        return left <= LifeLeftWarningPercent
            ? ($"{left}% left", $"{known.Explanation} The drive is near the end of its rated life, so plan to replace it.", AttributeStanding.Watch)
            : ($"{left}% left", known.Explanation, AttributeStanding.Normal);
    }

    private static string Value(AtaAttribute attribute) => $"Scored {attribute.Value}";

    private static string Key(AtaAttribute attribute) => $"ata-{attribute.Id}";

    private static string RawText(AtaAttribute attribute)
    {
        var threshold = attribute.Threshold is { } limit ? $", threshold {limit}" : string.Empty;

        return $"ID {attribute.Id}: value {attribute.Value}, worst {attribute.Worst}{threshold}, raw {attribute.Raw}";
    }
}
