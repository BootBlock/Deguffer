using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>
/// Reads a SATA or ATA drive. The attribute table and the drive's own failure prediction need no
/// elevation. Everything sent through a command handle does, and each is read where it is allowed.
/// </summary>
internal sealed class AtaHealthSource : IDriveHealthSource
{
    private const int AccessDenied = 5;

    /// <summary>The temperature attributes, the drive's own sensor first.</summary>
    private static readonly byte[] TemperatureIds = [194, 190];

    private static readonly IReadOnlyDictionary<int, string> LinkSpeeds = new Dictionary<int, string>
    {
        [1] = "1.5 Gb/s",
        [2] = "3 Gb/s",
        [3] = "6 Gb/s",
    };

    public bool Reads(StorageBusType bus) => bus is StorageBusType.Sata or StorageBusType.Ata;

    public DriveReadout Read(IDriveDevice device, string model)
    {
        var thresholds = device.Ask(DriveQuestion.AtaThresholds);
        var moreWhenElevated = thresholds.Win32Error == AccessDenied;
        var facts = IdentifyFacts(device);

        if (device.Ask(DriveQuestion.PredictFailure).Bytes is not { } prediction
            || !AtaAttributeTable.TryReadPrediction(prediction, out var predictsFailure, out var attributes))
        {
            return new DriveReadout([], HasPrimarySignal: false, facts, TemperatureCelsius: null, moreWhenElevated);
        }

        if (thresholds.Bytes is { } answer
            && AtaCommandAnswer.TrySector(answer, out var sector)
            && AtaAttributeTable.TryReadThresholds(sector, out var limits))
        {
            attributes = AtaAttributeTable.WithThresholds(attributes, limits);
        }

        List<HealthAttribute> rows = [AtaAttributeRows.Prediction(predictsFailure), .. AtaAttributeRows.Of(attributes, model)];

        if (Sector(device, DriveQuestion.AtaSelfTestLog) is { } selfTest && SelfTestLogs.TryReadAta(selfTest, out var outcome))
        {
            rows.Add(AtaAttributeRows.SelfTest(outcome));
        }

        if (Sector(device, DriveQuestion.AtaErrorLog) is { } errorLog && AtaErrorLog.TryReadCount(errorLog, out var count))
        {
            rows.Add(AtaAttributeRows.ErrorCount(count));
        }

        return new DriveReadout(rows, HasPrimarySignal: true, facts, Temperature(attributes), moreWhenElevated);
    }

    private static List<DriveFact> IdentifyFacts(IDriveDevice device)
    {
        var facts = new List<DriveFact>();

        if (Sector(device, DriveQuestion.AtaIdentify) is not { } sector || !AtaIdentify.TryRead(sector, out var identify))
        {
            return facts;
        }

        if (identify.RotationRpm is { } rpm)
        {
            facts.Add(new DriveFact("Rotation speed", rpm == 0 ? "Does not spin" : $"{rpm:N0} RPM"));
        }

        if (LinkSpeeds.TryGetValue(identify.CurrentGeneration, out var current))
        {
            var fastest = LinkSpeeds.GetValueOrDefault(identify.FastestGeneration, current);
            facts.Add(new DriveFact("SATA link speed", $"{current} now, up to {fastest}"));
        }

        return facts;
    }

    private static byte[]? Sector(IDriveDevice device, DriveQuestion question) =>
        device.Ask(question).Bytes is { } answer && AtaCommandAnswer.TrySector(answer, out var sector)
            ? sector.ToArray()
            : null;

    /// <summary>Most drives pack the lowest and highest seen above the low byte, so only that byte is read.</summary>
    private static int? Temperature(IReadOnlyList<AtaAttribute> attributes)
    {
        foreach (var id in TemperatureIds)
        {
            if (attributes.FirstOrDefault(attribute => attribute.Id == id) is { Id: not 0 } found && (int)(found.Raw & 0xFF) is var celsius and > 0)
            {
                return celsius;
            }
        }

        return null;
    }
}
