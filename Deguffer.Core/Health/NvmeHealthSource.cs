using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>Reads an NVMe drive. Every question here needs no elevation.</summary>
internal sealed class NvmeHealthSource : IDriveHealthSource
{
    public bool Reads(StorageBusType bus) => bus is StorageBusType.Nvme;

    public DriveReadout Read(IDriveDevice device, string model)
    {
        NvmeController? controller = device.Ask(DriveQuestion.NvmeController).Bytes is { } identify
            && NvmeController.TryRead(identify, out var read) ? read : null;

        IReadOnlyList<DriveFact> facts = controller is { } known
            ? [new DriveFact("NVMe version", $"{known.Version.Major}.{known.Version.Minor}")]
            : [];

        NvmeHealthLog? log = null;

        if (device.Ask(DriveQuestion.NvmeHealth).Bytes is not { } health || !NvmeHealthLog.TryRead(health, out log) || log is null)
        {
            return new DriveReadout([], HasPrimarySignal: false, facts, TemperatureCelsius: null, MoreWhenElevated: false);
        }

        var attributes = NvmeAttributeRows.Of(log, controller);

        if (device.Ask(DriveQuestion.NvmeSelfTest).Bytes is { } selfTest && SelfTestLogs.TryReadNvme(selfTest, out var outcome))
        {
            attributes = [.. attributes, AtaAttributeRows.SelfTest(outcome)];
        }

        return new DriveReadout(attributes, HasPrimarySignal: true, facts, HealthText.Celsius(log.TemperatureKelvin), MoreWhenElevated: false);
    }
}
